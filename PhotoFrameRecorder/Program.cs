using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Vortice.DXGI;

namespace PhotoFrameRecorder;

internal static class Program
{
    private const int AcquireTimeoutMs = 500;

    // Peak queue memory = capacity × frame size (a 4K BGRA frame is ~33 MB).
    private const int SaveQueueCapacity = 64;

    private const nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: PhotoFrameRecorder <output-folder>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Captures every frame the monitor displays (DXGI desktop duplication),");
            Console.Error.WriteLine("saving lossless PNGs named yyyyMMdd-N.png into <output-folder>.");
            Console.Error.WriteLine("Recording stops when the whole screen shows the termination colour");
            Console.Error.WriteLine("configured in App.config (default #000000), or on Ctrl+C.");
            return 1;
        }

        string outputFolder = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outputFolder);

        Color termination = LoadTerminationColor();

        // Keep coordinate spaces honest for the Screen enumeration below.
        // Must run before the first Screen.AllScreens call, which caches its results.
        if (!SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
        {
            SetProcessDPIAware();
        }

        var screens = System.Windows.Forms.Screen.AllScreens;
        Console.WriteLine("Detected screens:");
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            Console.WriteLine($"  {i + 1}: {screens[i].DeviceName} {b.Width}x{b.Height} at ({b.X},{b.Y})" +
                              (screens[i].Primary ? " [primary]" : ""));
        }

        int screenIndex = LoadCaptureScreenIndex();
        System.Windows.Forms.Screen? captureScreen = screenIndex switch
        {
            0 => System.Windows.Forms.Screen.PrimaryScreen ?? screens[0],
            _ when screenIndex <= screens.Length => screens[screenIndex - 1],
            _ => null,
        };
        if (captureScreen is null)
        {
            Console.Error.WriteLine($"CaptureScreen {screenIndex} in App.config is out of range - " +
                                    $"only {screens.Length} screen(s) detected.");
            return 1;
        }

        DesktopDuplicator duplicator;
        try
        {
            duplicator = DesktopDuplicator.Create(captureScreen.DeviceName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DXGI desktop duplication unavailable for {captureScreen.DeviceName}: {ex.Message}");
            return 1;
        }

        using var _ = duplicator;
        int width = duplicator.Width;
        int height = duplicator.Height;
        int frameBytes = width * height * 4;

        Console.WriteLine($"Recording {captureScreen.DeviceName} ({width}x{height}) to {outputFolder}");
        Console.WriteLine($"Stops when the screen is uniformly {ColorTranslator.ToHtml(termination)}, or on Ctrl+C.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Reuse frame buffers: at refresh rate a 4K stream would otherwise allocate ~2 GB/s.
        var bufferPool = new ConcurrentBag<byte[]>();
        byte[] RentBuffer() => bufferPool.TryTake(out var b) ? b : new byte[frameBytes];

        // PNG encoding is far slower than capture, so a bank of parallel encoders
        // drains a bounded queue. Filenames are assigned at capture time, so
        // out-of-order encoding cannot reorder the sequence.
        var saveQueue = Channel.CreateBounded<(byte[] Buffer, string Path)>(
            new BoundedChannelOptions(SaveQueueCapacity) { FullMode = BoundedChannelFullMode.Wait });
        long saved = 0;
        int encoderCount = Math.Clamp(Environment.ProcessorCount - 2, 2, 16);
        var encoders = Enumerable.Range(0, encoderCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var (buffer, path) in saveQueue.Reader.ReadAllAsync())
            {
                try
                {
                    var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        using var bmp = new Bitmap(width, height, width * 4,
                            PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
                        bmp.Save(path, ImageFormat.Png);
                    }
                    finally
                    {
                        handle.Free();
                    }
                    Interlocked.Increment(ref saved);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to save {Path.GetFileName(path)}: {ex.Message}");
                }
                bufferPool.Add(buffer);
            }
        })).ToArray();

        string date = DateTime.Now.ToString("yyyyMMdd");
        int sequence = NextSequence(outputFolder, date);
        long captured = 0;
        bool queueFullWarned = false;
        var statusWatch = Stopwatch.StartNew();
        (long, long, long) lastStatus = (-1, -1, -1);

        while (!cts.IsCancellationRequested)
        {
            byte[] buffer = RentBuffer();

            if (!duplicator.TryAcquireFrame(buffer, AcquireTimeoutMs))
            {
                bufferPool.Add(buffer);
            }
            else if (IsUniform(buffer, width * height, termination))
            {
                bufferPool.Add(buffer);
                Console.WriteLine("Termination colour detected - stopping.");
                break;
            }
            else
            {
                string today = DateTime.Now.ToString("yyyyMMdd");
                if (today != date)
                {
                    date = today;
                    sequence = NextSequence(outputFolder, date);
                }

                string path = Path.Combine(outputFolder, $"{date}-{sequence}.png");
                sequence++;
                captured++;

                if (!saveQueue.Writer.TryWrite((buffer, path)))
                {
                    if (!queueFullWarned)
                    {
                        queueFullWarned = true;
                        Console.WriteLine("Warning: PNG encoders can't keep up - capture will stall " +
                                          "until the queue drains, and frames may be missed.");
                    }
                    await saveQueue.Writer.WriteAsync((buffer, path));
                }
            }

            if (statusWatch.ElapsedMilliseconds >= 1000)
            {
                statusWatch.Restart();
                var status = (captured, Volatile.Read(ref saved), duplicator.MissedFrames);
                if (status != lastStatus)
                {
                    lastStatus = status;
                    Console.WriteLine($"captured {status.Item1}, saved {status.Item2}, " +
                                      $"queued {status.Item1 - status.Item2}, missed {status.Item3}");
                }
            }
        }

        saveQueue.Writer.Complete();
        await Task.WhenAll(encoders);

        Console.WriteLine($"Recorder finished: {Volatile.Read(ref saved)} frame(s) saved" +
                          (duplicator.MissedFrames > 0
                              ? $", {duplicator.MissedFrames} frame(s) missed (see AccumulatedFrames)."
                              : ", no frames missed."));
        return 0;
    }

    /// <summary>
    /// True when every pixel matches the termination colour (alpha ignored - desktop
    /// duplication leaves it undefined). SIMD scan via IndexOfAnyExcept.
    /// </summary>
    private static bool IsUniform(byte[] buffer, int pixelCount, Color termination)
    {
        uint rgb = ((uint)termination.R << 16) | ((uint)termination.G << 8) | termination.B;
        var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, pixelCount * 4));
        return pixels.IndexOfAnyExcept(0xFF000000u | rgb, rgb) < 0;
    }

    /// <summary>Continues today's numbering if the folder already has captures for the date.</summary>
    private static int NextSequence(string folder, string date)
    {
        int max = 0;
        foreach (string file in Directory.EnumerateFiles(folder, $"{date}-*.png"))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(stem.AsSpan(date.Length + 1), out int n) && n > max)
            {
                max = n;
            }
        }
        return max + 1;
    }

    /// <summary>Reads CaptureScreen from App.config: 0 (or missing) means the primary screen.</summary>
    private static int LoadCaptureScreenIndex()
    {
        try
        {
            string? text = ConfigurationManager.AppSettings["CaptureScreen"];
            if (int.TryParse(text, out int value) && value >= 0)
            {
                return value;
            }
        }
        catch (Exception)
        {
            // Missing or malformed config - fall back to the primary screen.
        }
        return 0;
    }

    private static Color LoadTerminationColor()
    {
        try
        {
            string? text = ConfigurationManager.AppSettings["TerminationColor"];
            if (!string.IsNullOrWhiteSpace(text))
            {
                return ColorTranslator.FromHtml(text);
            }
        }
        catch (Exception)
        {
            // Missing or malformed config - fall back to the default below.
        }
        return Color.Black;
    }
}
