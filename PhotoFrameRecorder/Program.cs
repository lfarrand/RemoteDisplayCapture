using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Vector = System.Numerics.Vector;

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

    private static long s_saved;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: PhotoFrameRecorder <output-folder>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Captures every frame the monitor displays (DXGI desktop duplication),");
            Console.Error.WriteLine("saving lossless images named yyyyMMdd-N into <output-folder> in the");
            Console.Error.WriteLine("format configured in App.config (png, tiff, or bmp). Recording stops");
            Console.Error.WriteLine("when the whole screen shows the termination colour configured in");
            Console.Error.WriteLine("App.config (default #000000), or on Ctrl+C.");
            return 1;
        }

        string outputFolder = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outputFolder);

        Color termination = LoadTerminationColor();
        int terminationTolerance = LoadTerminationTolerance();

        string? format = LoadOutputFormat();
        if (format is null)
        {
            Console.Error.WriteLine($"OutputFormat '{ConfigurationManager.AppSettings["OutputFormat"]}' " +
                                    "in App.config is not supported. Lossless formats: png, tiff, bmp.");
            return 1;
        }
        string extension = format == "tiff" ? "tif" : format;

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

        Console.WriteLine($"Recording {captureScreen.DeviceName} ({width}x{height}) to {outputFolder} as .{extension}");
        Console.WriteLine($"Stops when the screen is uniformly {ColorTranslator.ToHtml(termination)} " +
                          $"(±{terminationTolerance} per channel), or on Ctrl+C.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Reuse frame buffers: at refresh rate a 4K stream would otherwise allocate ~2 GB/s.
        // Pre-warm the pool - committing and zeroing a 33 MB array mid-capture costs
        // more than a frame interval, which is exactly how startup bursts drop frames.
        var bufferPool = new ConcurrentBag<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            bufferPool.Add(new byte[frameBytes]);
        }
        byte[] RentBuffer() => bufferPool.TryTake(out var b) ? b : new byte[frameBytes];

        // Encoding is far slower than capture, so a bank of encoder threads drains a
        // bounded queue. Filenames are assigned at capture time, so out-of-order
        // encoding cannot reorder the sequence. Workers are dedicated threads (not
        // thread-pool tasks) because each owns a reusable, thread-affine WriteableBitmap.
        using var saveQueue = new BlockingCollection<(byte[] Buffer, string Path)>(SaveQueueCapacity);
        int encoderCount = Math.Max(2, Environment.ProcessorCount - 2);
        var encoders = new Thread[encoderCount];
        for (int i = 0; i < encoderCount; i++)
        {
            encoders[i] = new Thread(() => EncodeWorker(saveQueue, bufferPool, width, height, format))
            {
                IsBackground = true,
                Name = $"encoder-{i}",
            };
            encoders[i].Start();
        }

        // The capture loop must never be late to AcquireNextFrame: keep it ahead of
        // the encoder threads in the scheduler and defer blocking GCs while recording.
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        string date = DateTime.Now.ToString("yyyyMMdd");
        int sequence = NextSequence(outputFolder, date, extension);
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
            else if (IsUniform(buffer, width * height, termination, terminationTolerance))
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
                    sequence = NextSequence(outputFolder, date, extension);
                }

                string path = Path.Combine(outputFolder, $"{date}-{sequence}.{extension}");
                sequence++;
                captured++;

                if (!saveQueue.TryAdd((buffer, path)))
                {
                    if (!queueFullWarned)
                    {
                        queueFullWarned = true;
                        Console.WriteLine("Warning: encoders can't keep up - capture will stall " +
                                          "until the queue drains, and frames may be missed.");
                    }
                    saveQueue.Add((buffer, path));
                }
            }

            if (statusWatch.ElapsedMilliseconds >= 1000)
            {
                statusWatch.Restart();
                var status = (captured, Volatile.Read(ref s_saved), duplicator.MissedFrames);
                if (status != lastStatus)
                {
                    lastStatus = status;
                    Console.WriteLine($"captured {status.Item1}, saved {status.Item2}, " +
                                      $"queued {status.Item1 - status.Item2}, missed {status.Item3}");
                }
            }
        }

        saveQueue.CompleteAdding();
        foreach (Thread encoder in encoders)
        {
            encoder.Join();
        }

        Console.WriteLine($"Recorder finished: {Volatile.Read(ref s_saved)} frame(s) saved" +
                          (duplicator.MissedFrames > 0
                              ? $", {duplicator.MissedFrames} frame(s) missed (see AccumulatedFrames)."
                              : ", no frames missed."));
        return 0;
    }

    private static void EncodeWorker(BlockingCollection<(byte[] Buffer, string Path)> queue,
        ConcurrentBag<byte[]> bufferPool, int width, int height, string format)
    {
        // One reusable WIC bitmap per worker: WritePixels overwrites the same native
        // buffer every frame instead of allocating (and later finalizing) ~33 MB of
        // fresh WIC memory per frame. WriteableBitmap is thread-affine, which is why
        // this worker is a dedicated thread.
        var bitmap = new WriteableBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgr32, null);
        var fullFrame = new Int32Rect(0, 0, width, height);

        foreach (var (buffer, path) in queue.GetConsumingEnumerable())
        {
            try
            {
                bitmap.WritePixels(fullFrame, buffer, width * 4, 0);
                // The pixels are now in the bitmap; recycle the buffer before the
                // (comparatively slow) encode runs.
                bufferPool.Add(buffer);

                BitmapEncoder encoder = CreateEncoder(format);
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1 << 20);
                encoder.Save(stream);
                Interlocked.Increment(ref s_saved);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to save {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private static BitmapEncoder CreateEncoder(string format) => format switch
    {
        "png" => new PngBitmapEncoder(),
        "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        "bmp" => new BmpBitmapEncoder(),
        _ => throw new InvalidOperationException($"unknown format {format}"),
    };

    /// <summary>
    /// True when the frame is the termination screen: every pixel uniform (within a
    /// small epsilon of the frame's own first pixel - HDR tone mapping shifts colours
    /// but does so uniformly across a solid screen) AND that uniform colour within
    /// the configured per-channel tolerance of the termination colour. Alpha is
    /// ignored; desktop duplication leaves it undefined. Tolerance 0 = exact match.
    /// </summary>
    private static bool IsUniform(byte[] buffer, int pixelCount, Color termination, int tolerance)
    {
        if (tolerance == 0)
        {
            uint rgb = ((uint)termination.R << 16) | ((uint)termination.G << 8) | termination.B;
            var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, pixelCount * 4));
            return pixels.IndexOfAnyExcept(0xFF000000u | rgb, rgb) < 0;
        }

        // The tone-mapped colour must still resemble the configured one.
        byte b0 = buffer[0], g0 = buffer[1], r0 = buffer[2];
        if (Math.Abs(r0 - termination.R) > tolerance ||
            Math.Abs(g0 - termination.G) > tolerance ||
            Math.Abs(b0 - termination.B) > tolerance)
        {
            return false;
        }

        // The rest of the frame must match the first pixel almost exactly.
        const int UniformEpsilon = 2;
        byte bLow = (byte)Math.Max(b0 - UniformEpsilon, 0);
        byte bHigh = (byte)Math.Min(b0 + UniformEpsilon, 255);
        byte gLow = (byte)Math.Max(g0 - UniformEpsilon, 0);
        byte gHigh = (byte)Math.Min(g0 + UniformEpsilon, 255);
        byte rLow = (byte)Math.Max(r0 - UniformEpsilon, 0);
        byte rHigh = (byte)Math.Min(r0 + UniformEpsilon, 255);

        // Bounds vectors repeating the BGRA channel pattern (alpha bounds 0..255).
        int lanes = Vector<byte>.Count;
        Span<byte> lowPattern = stackalloc byte[lanes];
        Span<byte> highPattern = stackalloc byte[lanes];
        for (int lane = 0; lane < lanes; lane++)
        {
            (lowPattern[lane], highPattern[lane]) = (lane % 4) switch
            {
                0 => (bLow, bHigh),
                1 => (gLow, gHigh),
                2 => (rLow, rHigh),
                _ => ((byte)0, (byte)255),
            };
        }
        var low = new Vector<byte>(lowPattern);
        var high = new Vector<byte>(highPattern);

        var span = buffer.AsSpan(0, pixelCount * 4);
        int i = 0;
        for (; i <= span.Length - lanes; i += lanes)
        {
            var v = new Vector<byte>(span.Slice(i, lanes));
            if (!Vector.EqualsAll(Vector.Max(v, low), v) || !Vector.EqualsAll(Vector.Min(v, high), v))
            {
                return false;
            }
        }
        for (; i < span.Length; i += 4)
        {
            if (span[i] < bLow || span[i] > bHigh ||
                span[i + 1] < gLow || span[i + 1] > gHigh ||
                span[i + 2] < rLow || span[i + 2] > rHigh)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Reads TerminationTolerance from App.config, clamped to 0-255; default 30.</summary>
    private static int LoadTerminationTolerance()
    {
        try
        {
            string? text = ConfigurationManager.AppSettings["TerminationTolerance"];
            if (int.TryParse(text, out int value))
            {
                return Math.Clamp(value, 0, 255);
            }
        }
        catch (Exception)
        {
            // Missing or malformed config - fall back to the default below.
        }
        return 30;
    }

    /// <summary>Continues today's numbering if the folder already has captures for the date.</summary>
    private static int NextSequence(string folder, string date, string extension)
    {
        int max = 0;
        foreach (string file in Directory.EnumerateFiles(folder, $"{date}-*.{extension}"))
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

    /// <summary>Normalized output format from App.config, or null when unsupported.</summary>
    private static string? LoadOutputFormat()
    {
        string? text;
        try
        {
            text = ConfigurationManager.AppSettings["OutputFormat"];
        }
        catch (Exception)
        {
            text = null;
        }

        return (text?.Trim().ToLowerInvariant() ?? "png") switch
        {
            "png" => "png",
            "tif" or "tiff" => "tiff",
            "bmp" => "bmp",
            _ => null,
        };
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
