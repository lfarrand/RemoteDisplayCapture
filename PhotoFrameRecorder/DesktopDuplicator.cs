using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PhotoFrameRecorder;

/// <summary>
/// Wraps DXGI desktop duplication for one monitor: hands over each frame the
/// compositor presents, exactly once, as tightly-packed 32bpp BGRA pixels.
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    // How long to wait for the next present while a frame's GPU copy is still in
    // flight. The wait doubles as pipelining: the copy completes while we sit in
    // AcquireNextFrame, so the subsequent Map doesn't stall on the GPU.
    private const int PipelinePollMs = 4;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutput1 _output;
    private readonly ID3D11Texture2D[] _staging;
    private IDXGIOutputDuplication _duplication;

    // Staging slot holding a copied-but-not-yet-read frame, or -1 when empty.
    private int _pendingSlot = -1;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Frames the compositor presented while we weren't ready to receive them.</summary>
    public long MissedFrames { get; private set; }

    private DesktopDuplicator(ID3D11Device device, ID3D11DeviceContext context,
        IDXGIOutput1 output, IDXGIOutputDuplication duplication, ID3D11Texture2D[] staging,
        int width, int height)
    {
        _device = device;
        _context = context;
        _output = output;
        _duplication = duplication;
        _staging = staging;
        Width = width;
        Height = height;
    }

    public static DesktopDuplicator Create(string deviceName)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0;
             factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success;
             adapterIndex++)
        {
            for (uint outputIndex = 0;
                 adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success;
                 outputIndex++)
            {
                if (!string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    output.Dispose();
                    continue;
                }

                try
                {
                    return CreateForOutput(adapter, output);
                }
                finally
                {
                    output.Dispose();
                    adapter.Dispose();
                }
            }
            adapter.Dispose();
        }

        throw new InvalidOperationException($"no DXGI output is named {deviceName}.");
    }

    private static DesktopDuplicator CreateForOutput(IDXGIAdapter1 adapter, IDXGIOutput output)
    {
        // The device must live on the adapter that owns the output being duplicated.
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [], out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();

        IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
        try
        {
            IDXGIOutputDuplication duplication = output1.DuplicateOutput(device!);

            var mode = duplication.Description.ModeDescription;
            int width = (int)mode.Width;
            int height = (int)mode.Height;

            if (duplication.Description.Rotation != ModeRotation.Identity)
            {
                Console.WriteLine($"Warning: {output.Description.DeviceName} is rotated " +
                                  $"({duplication.Description.Rotation}); captures are stored unrotated.");
            }

            // Two staging textures let the GPU copy frame N while the CPU reads N-1.
            var staging = new ID3D11Texture2D[2];
            for (int i = 0; i < staging.Length; i++)
            {
                staging[i] = device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
            }

            return new DesktopDuplicator(device!, context!, output1, duplication, staging, width, height);
        }
        catch
        {
            output1.Dispose();
            context?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Blocks until the compositor presents a new frame (or the timeout elapses) and
    /// copies it, tightly packed, into <paramref name="buffer"/>. Returns false on
    /// timeout. Pointer-only updates are skipped; duplication access loss (display
    /// mode changes, fullscreen exclusive switches) is recovered automatically.
    /// Frames flow through a two-slot staging ring so the GPU copy of one frame
    /// overlaps the CPU readback of the previous one (adds at most
    /// <see cref="PipelinePollMs"/> latency, which is irrelevant for saving files).
    /// </summary>
    public bool TryAcquireFrame(byte[] buffer, int timeoutMs)
    {
        while (true)
        {
            int wait = _pendingSlot >= 0 ? PipelinePollMs : timeoutMs;
            Result result = _duplication.AcquireNextFrame((uint)wait,
                out OutduplFrameInfo info, out IDXGIResource? resource);

            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                if (_pendingSlot >= 0)
                {
                    // No new present within the poll window - drain the frame whose
                    // GPU copy has been running during the wait.
                    int ready = _pendingSlot;
                    _pendingSlot = -1;
                    ReadStaging(ready, buffer);
                    return true;
                }
                return false;
            }
            if (result == Vortice.DXGI.ResultCode.AccessLost)
            {
                // The staging ring lives on our device and survives; only the
                // duplication needs recreating. Any pending frame stays drainable.
                RecreateDuplication();
                continue;
            }
            result.CheckError();

            int slot;
            using (resource)
            {
                // LastPresentTime == 0 means only the mouse pointer changed;
                // the desktop image itself is not new.
                if (info.LastPresentTime == 0)
                {
                    _duplication.ReleaseFrame();
                    continue;
                }

                slot = _pendingSlot < 0 ? 0 : 1 - _pendingSlot;
                using ID3D11Texture2D texture = resource!.QueryInterface<ID3D11Texture2D>();
                _context.CopyResource(_staging[slot], texture);
            }
            _duplication.ReleaseFrame();

            if (info.AccumulatedFrames > 1)
            {
                MissedFrames += info.AccumulatedFrames - 1;
            }

            if (_pendingSlot >= 0)
            {
                // Read the older frame while the copy just issued runs on the GPU.
                int ready = _pendingSlot;
                _pendingSlot = slot;
                ReadStaging(ready, buffer);
                return true;
            }

            // First frame into an empty pipeline: hold it and poll briefly for the
            // next present so its GPU copy completes while we wait.
            _pendingSlot = slot;
        }
    }

    private void ReadStaging(int slot, byte[] buffer)
    {
        MappedSubresource mapped = _context.Map(_staging[slot], 0, MapMode.Read);
        try
        {
            int tightStride = Width * 4;
            if ((int)mapped.RowPitch == tightStride)
            {
                Marshal.Copy(mapped.DataPointer, buffer, 0, tightStride * Height);
            }
            else
            {
                nint source = mapped.DataPointer;
                for (int y = 0; y < Height; y++)
                {
                    Marshal.Copy(source + y * (nint)mapped.RowPitch, buffer, y * tightStride, tightStride);
                }
            }
        }
        finally
        {
            _context.Unmap(_staging[slot], 0);
        }
    }

    private void RecreateDuplication()
    {
        _duplication.Dispose();
        // Access loss is transient (mode change, UAC desktop, fullscreen switch);
        // give the compositor a moment before re-attaching.
        for (int attempt = 1; ; attempt++)
        {
            Thread.Sleep(100);
            try
            {
                _duplication = _output.DuplicateOutput(_device);
                return;
            }
            catch (Exception) when (attempt < 50)
            {
                // Keep retrying for ~5 seconds before surfacing the failure.
            }
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        foreach (var staging in _staging)
        {
            staging.Dispose();
        }
        _output.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
