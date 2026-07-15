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

    // Peak queue memory = capacity × frame size (a 4K BGRA frame is ~33 MB;
    // an FP16 HDR frame is ~66 MB, so HDR mode halves the queue and pool).
    private const int SaveQueueCapacitySdr = 64;
    private const int SaveQueueCapacityHdr = 16;

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
        bool hdrCapture = LoadBoolSetting("HdrCapture");

        string? format = LoadOutputFormat();
        if (format is null)
        {
            Console.Error.WriteLine($"OutputFormat '{ConfigurationManager.AppSettings["OutputFormat"]}' " +
                                    "in App.config is not supported. Lossless formats: png, tiff, bmp.");
            return 1;
        }
        if (hdrCapture && format != "tiff")
        {
            Console.WriteLine($"HdrCapture stores 32-bit float scRGB, which only TIFF holds - " +
                              $"OutputFormat '{format}' is ignored.");
            format = "tiff";
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
            duplicator = DesktopDuplicator.Create(captureScreen.DeviceName, hdrCapture);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DXGI desktop duplication unavailable for {captureScreen.DeviceName}: {ex.Message}" +
                                    (ex.InnerException is { } inner ? $" [{inner.Message}]" : ""));
            return 1;
        }

        using var _ = duplicator;
        int width = duplicator.Width;
        int height = duplicator.Height;
        int frameBytes = width * height * duplicator.BytesPerPixel;
        CapturePixelFormat pixelKind = duplicator.PixelFormat;

        Console.WriteLine($"Recording {captureScreen.DeviceName} ({width}x{height}) to {outputFolder} as .{extension}" +
                          (hdrCapture ? $" ({pixelKind} -> 32-bit float scRGB TIFF)" : ""));
        if (duplicator.ColorSpaceNote is { } colorSpaceNote)
        {
            Console.WriteLine(colorSpaceNote);
        }
        Console.WriteLine($"Stops when the screen is uniformly {ColorTranslator.ToHtml(termination)} " +
                          $"(+/-{terminationTolerance} per channel), or on Ctrl+C.");

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
        for (int i = 0; i < (hdrCapture ? 4 : 8); i++)
        {
            bufferPool.Add(new byte[frameBytes]);
        }
        byte[] RentBuffer() => bufferPool.TryTake(out var b) ? b : new byte[frameBytes];

        // Encoding is far slower than capture, so a bank of encoder threads drains a
        // bounded queue. Filenames are assigned at capture time, so out-of-order
        // encoding cannot reorder the sequence. Workers are dedicated threads (not
        // thread-pool tasks) because each owns a reusable, thread-affine WriteableBitmap.
        using var saveQueue = new BlockingCollection<(byte[] Buffer, string Path)>(
            hdrCapture ? SaveQueueCapacityHdr : SaveQueueCapacitySdr);
        int encoderCount = Math.Max(2, Environment.ProcessorCount - 2);
        var encoders = new Thread[encoderCount];
        for (int i = 0; i < encoderCount; i++)
        {
            encoders[i] = new Thread(() => EncodeWorker(saveQueue, bufferPool, width, height, format, pixelKind))
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
            else if (pixelKind switch
            {
                CapturePixelFormat.Fp16 => IsUniformFp16(buffer, width * height, termination, terminationTolerance),
                CapturePixelFormat.Pq10 => IsUniformPq10(buffer, width * height, termination, terminationTolerance),
                _ => IsUniform(buffer, width * height, termination, terminationTolerance),
            })
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
        ConcurrentBag<byte[]> bufferPool, int width, int height, string format, CapturePixelFormat kind)
    {
        // One reusable WIC bitmap per worker: WritePixels overwrites the same native
        // buffer every frame instead of allocating (and later finalizing) ~33 MB of
        // fresh WIC memory per frame. WriteableBitmap is thread-affine, which is why
        // this worker is a dedicated thread. HDR frames (FP16 scRGB or 10-bit
        // PQ/BT.2020, whichever the compositor uses) are normalised to Rgba128Float
        // linear scRGB, the float format WPF's TIFF encoder stores.
        bool hdr = kind != CapturePixelFormat.Bgra8;
        WriteableBitmap? bitmap = hdr
            ? null
            : new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        var fullFrame = new Int32Rect(0, 0, width, height);
        // HDR frames (FP16 scRGB or 10-bit PQ/BT.2020) are normalised to linear
        // scRGB floats and written by FloatTiffWriter - WPF's TIFF encoder cannot
        // hold float pixels (it silently converts them to 8-bit).
        float[]? floats = hdr ? new float[width * height * 4] : null;

        foreach (var (buffer, path) in queue.GetConsumingEnumerable())
        {
            try
            {
                if (hdr)
                {
                    if (kind == CapturePixelFormat.Fp16)
                    {
                        var halfs = MemoryMarshal.Cast<byte, Half>(buffer.AsSpan(0, width * height * 8));
                        for (int i = 0; i < floats!.Length; i++)
                        {
                            floats[i] = (float)halfs[i];
                        }
                    }
                    else
                    {
                        ConvertPq10ToScRgb(buffer, floats!, width * height);
                    }
                    bufferPool.Add(buffer);

                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                        FileShare.None, 1 << 20);
                    FloatTiffWriter.Write(stream, floats!, width, height);
                }
                else
                {
                    bitmap!.WritePixels(fullFrame, buffer, width * 4, 0);
                    // The pixels are now in the bitmap; recycle the buffer before
                    // the (comparatively slow) encode runs.
                    bufferPool.Add(buffer);

                    BitmapEncoder encoder = CreateEncoder(format);
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                        FileShare.None, 1 << 20);
                    encoder.Save(stream);
                }
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

        // Sparse prefilter: ~1000 scattered pixels reject any content frame in
        // microseconds. Without it, a frame whose top border resembles the
        // termination colour pays a multi-millisecond scan on every capture -
        // enough to make the capture loop miss presents.
        int sampleStep = Math.Max(1, pixelCount / 1024) * 4;
        for (int s = 0; s < pixelCount * 4; s += sampleStep)
        {
            if (buffer[s] < bLow || buffer[s] > bHigh ||
                buffer[s + 1] < gLow || buffer[s + 1] > gHigh ||
                buffer[s + 2] < rLow || buffer[s + 2] > rHigh)
            {
                return false;
            }
        }

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

    // PQ (SMPTE ST 2084) code value -> linear scRGB (nits / 80). 10-bit input means
    // only 1024 possible codes, so the EOTF collapses to a table lookup.
    private static readonly Lazy<float[]> s_pqToScRgb = new(() =>
    {
        const double m1 = 0.1593017578125;
        const double m2 = 78.84375;
        const double c1 = 0.8359375;
        const double c2 = 18.8515625;
        const double c3 = 18.6875;
        var lut = new float[1024];
        for (int code = 0; code < 1024; code++)
        {
            double e = Math.Pow(code / 1023.0, 1.0 / m2);
            double y = Math.Pow(Math.Max(e - c1, 0.0) / (c2 - c3 * e), 1.0 / m1);
            lut[code] = (float)(y * 10000.0 / 80.0);
        }
        return lut;
    });

    /// <summary>
    /// 10-bit PQ/BT.2020 pixels to linear scRGB floats: PQ decode via lookup table,
    /// then the BT.2020 -> BT.709 primaries matrix (out-of-gamut colours go negative,
    /// which scRGB permits - nothing is clipped).
    /// </summary>
    private static void ConvertPq10ToScRgb(byte[] buffer, float[] floats, int pixelCount)
    {
        float[] lut = s_pqToScRgb.Value;
        var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, pixelCount * 4));
        for (int i = 0; i < pixels.Length; i++)
        {
            uint v = pixels[i];
            float r = lut[v & 0x3FF];
            float g = lut[(v >> 10) & 0x3FF];
            float b = lut[(v >> 20) & 0x3FF];
            int o = i * 4;
            floats[o] = 1.6605f * r - 0.5876f * g - 0.0728f * b;
            floats[o + 1] = -0.1246f * r + 1.1329f * g - 0.0083f * b;
            floats[o + 2] = -0.0182f * r - 0.1006f * g + 1.1187f * b;
            floats[o + 3] = 1f;
        }
    }

    /// <summary>
    /// Termination check on 10-bit PQ/BT.2020 pixels: uniform relative to the first
    /// pixel (within 2 code values per channel), with the first pixel - converted to
    /// 8-bit sRGB - within tolerance of the termination colour. As with FP16, black
    /// is the reliable termination colour choice.
    /// </summary>
    private static bool IsUniformPq10(byte[] buffer, int pixelCount, Color termination, int tolerance)
    {
        float[] lut = s_pqToScRgb.Value;
        var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, pixelCount * 4));
        uint first = pixels[0];
        int r0 = (int)(first & 0x3FF);
        int g0 = (int)((first >> 10) & 0x3FF);
        int b0 = (int)((first >> 20) & 0x3FF);

        float rLin = lut[r0];
        float gLin = lut[g0];
        float bLin = lut[b0];
        if (Math.Abs(ScRgbToSrgbByte(1.6605f * rLin - 0.5876f * gLin - 0.0728f * bLin) - termination.R) > tolerance ||
            Math.Abs(ScRgbToSrgbByte(-0.1246f * rLin + 1.1329f * gLin - 0.0083f * bLin) - termination.G) > tolerance ||
            Math.Abs(ScRgbToSrgbByte(-0.0182f * rLin - 0.1006f * gLin + 1.1187f * bLin) - termination.B) > tolerance)
        {
            return false;
        }

        const int UniformEpsilon = 2;
        bool Matches(uint v) =>
            Math.Abs((int)(v & 0x3FF) - r0) <= UniformEpsilon &&
            Math.Abs((int)((v >> 10) & 0x3FF) - g0) <= UniformEpsilon &&
            Math.Abs((int)((v >> 20) & 0x3FF) - b0) <= UniformEpsilon;

        // Sparse prefilter first - see the SDR path for why.
        int sampleStep = Math.Max(1, pixelCount / 1024);
        for (int s = 0; s < pixels.Length; s += sampleStep)
        {
            if (!Matches(pixels[s])) return false;
        }

        for (int i = 1; i < pixels.Length; i++)
        {
            if (!Matches(pixels[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// HDR-mode termination check on FP16 scRGB pixels (RGBA order): the frame must
    /// be uniform relative to its own first pixel, and that colour - encoded to 8-bit
    /// sRGB assuming SDR white at scRGB 1.0 - within tolerance of the termination
    /// colour. Black is exact regardless of the system's SDR white level, so black
    /// termination colours are the reliable choice in this mode.
    /// </summary>
    private static bool IsUniformFp16(byte[] buffer, int pixelCount, Color termination, int tolerance)
    {
        var pixels = MemoryMarshal.Cast<byte, Half>(buffer.AsSpan(0, pixelCount * 8));
        float r0 = (float)pixels[0];
        float g0 = (float)pixels[1];
        float b0 = (float)pixels[2];

        if (Math.Abs(ScRgbToSrgbByte(r0) - termination.R) > tolerance ||
            Math.Abs(ScRgbToSrgbByte(g0) - termination.G) > tolerance ||
            Math.Abs(ScRgbToSrgbByte(b0) - termination.B) > tolerance)
        {
            return false;
        }

        const float UniformEpsilon = 0.005f;

        // Sparse prefilter first - see the SDR path for why.
        int sampleStep = Math.Max(1, pixelCount / 1024) * 4;
        for (int s = 0; s < pixels.Length; s += sampleStep)
        {
            if (Math.Abs((float)pixels[s] - r0) > UniformEpsilon ||
                Math.Abs((float)pixels[s + 1] - g0) > UniformEpsilon ||
                Math.Abs((float)pixels[s + 2] - b0) > UniformEpsilon)
            {
                return false;
            }
        }

        for (int i = 4; i < pixels.Length; i += 4)
        {
            if (Math.Abs((float)pixels[i] - r0) > UniformEpsilon ||
                Math.Abs((float)pixels[i + 1] - g0) > UniformEpsilon ||
                Math.Abs((float)pixels[i + 2] - b0) > UniformEpsilon)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Linear scRGB channel to 8-bit sRGB, clamped to SDR range.</summary>
    private static int ScRgbToSrgbByte(float linear)
    {
        double l = Math.Clamp(linear, 0f, 1f);
        double s = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
        return (int)Math.Round(s * 255.0);
    }

    /// <summary>Reads a boolean setting from App.config; missing or malformed means false.</summary>
    private static bool LoadBoolSetting(string key)
    {
        try
        {
            return bool.TryParse(ConfigurationManager.AppSettings[key], out bool value) && value;
        }
        catch (Exception)
        {
            return false;
        }
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
