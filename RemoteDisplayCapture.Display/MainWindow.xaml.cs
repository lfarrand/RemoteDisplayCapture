using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RemoteDisplayCapture.Display;

public partial class MainWindow : Window
{
    // Above this rate we pre-decode every frame and drive playback from the render loop;
    // at or below it we stream images one ahead like a classic slideshow.
    private const double FlipbookThresholdFps = 5.0;
    private const double MaxFps = 1000.0;

    private static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif"];

    private readonly string _folder;
    private readonly bool _flipbookMode;
    private readonly long _memoryCapBytes;
    private readonly bool _playOnce;
    private readonly Color _terminationColor;
    private double _fps;
    private List<string> _imagePaths = [];
    private bool _paused;
    private bool _terminated;

    // Nominated monitor's bounds in physical pixels, and the DPI scale needed to map
    // image pixels 1:1 onto device pixels (WPF layout works in DPI-scaled units).
    private readonly System.Drawing.Rectangle _screenBounds;
    private DpiScale _dpi;
    private int _screenPixelWidth;
    private int _screenPixelHeight;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
    private const uint SwpNoZOrder = 0x0004;

    // Slideshow mode: timer-driven, decode one image ahead.
    private readonly DispatcherTimer _timer;
    private int _currentIndex = -1;
    private Task<BitmapSource?>? _preloadTask;
    private int _preloadIndex = -1;

    // Flipbook mode: all frames pre-decoded at full resolution; the displayed frame is
    // derived from a wall clock so playback stays time-accurate even when the monitor
    // can't show every frame. Oversized frames carry an error message instead of pixels.
    // How far playback may trail the wall clock before frames are skipped to
    // catch up. Within this bound every frame is shown (one per render tick);
    // beyond it - rates above the render tick rate, or a long stall - the
    // time-accurate index wins.
    private const long CatchUpFrames = 10;

    private BitmapSource?[] _frames = [];
    private string?[] _frameErrors = [];
    private readonly Stopwatch _clock = new();
    private double _clockOffsetSeconds;
    private TimeSpan _lastRenderTime = TimeSpan.MinValue;
    private int _lastShownFrame = -1;
    private long _lastRawIndex = -1;

    public MainWindow(string folder, double fps, long memoryCapBytes, bool playOnce,
        Color borderColor, Color terminationColor, System.Drawing.Rectangle screenBounds)
    {
        InitializeComponent();

        _folder = folder;
        _fps = fps;
        _memoryCapBytes = memoryCapBytes;
        _playOnce = playOnce;
        _terminationColor = terminationColor;
        _screenBounds = screenBounds;
        _flipbookMode = fps > FlipbookThresholdFps;
        Background = new SolidColorBrush(borderColor);

        // Place the window on the nominated monitor (physical pixels), then let
        // Maximized make the borderless window cover exactly that monitor.
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, 0, _screenBounds.X, _screenBounds.Y,
                _screenBounds.Width, _screenBounds.Height, SwpNoZOrder);
            WindowState = WindowState.Maximized;
        };
        DpiChanged += (_, e) => _dpi = e.NewDpi;

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => _ = ShowNextAsync();

        Loaded += async (_, _) => await StartAsync();
    }

    private double CurrentSeconds => _clockOffsetSeconds + _clock.Elapsed.TotalSeconds;

    private async Task StartAsync()
    {
        _dpi = VisualTreeHelper.GetDpi(this);
        _screenPixelWidth = _screenBounds.Width;
        _screenPixelHeight = _screenBounds.Height;

        _imagePaths = Directory.EnumerateFiles(_folder)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_imagePaths.Count == 0)
        {
            MessageBox.Show($"No images found in {_folder}",
                "RemoteDisplayCapture.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        if (_flipbookMode)
        {
            await StartFlipbookAsync();
        }
        else
        {
            await ShowNextAsync();
            _timer.Interval = TimeSpan.FromSeconds(1.0 / _fps);
            _timer.Start();
        }
    }

    /// <summary>
    /// Shows <paramref name="bitmap"/> mapped 1:1 onto device pixels, or an error
    /// message when the frame is rejected. A null bitmap with no error (undecodable
    /// file) leaves the previous frame on screen.
    /// </summary>
    private void DisplayFrame(BitmapSource? bitmap, string? error)
    {
        if (error is not null)
        {
            PhotoImage.Visibility = Visibility.Collapsed;
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (bitmap is null) return;

        ErrorText.Visibility = Visibility.Collapsed;
        // Explicit size in DPI-scaled units puts one image pixel on one screen pixel,
        // regardless of monitor scaling or the DPI metadata inside the file.
        PhotoImage.Width = bitmap.PixelWidth / _dpi.DpiScaleX;
        PhotoImage.Height = bitmap.PixelHeight / _dpi.DpiScaleY;
        PhotoImage.Source = bitmap;
        PhotoImage.Visibility = Visibility.Visible;
    }

    private string OversizeError(string path, int width, int height) =>
        $"{Path.GetFileName(path)} ({width}×{height}) exceeds the monitor display resolution " +
        $"({_screenPixelWidth}×{_screenPixelHeight}) — image not displayed.";

    // ---- Flipbook mode ----

    private async Task StartFlipbookAsync()
    {
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = "Scanning frames…";

        var errors = new string?[_imagePaths.Count];
        long estimatedBytes = await Task.Run(() => ScanFrames(errors));

        if (estimatedBytes > _memoryCapBytes)
        {
            MessageBox.Show(
                $"Decoding all {_imagePaths.Count} frames at full resolution needs about " +
                $"{FormatBytes(estimatedBytes)}, which exceeds the {FormatBytes(_memoryCapBytes)} memory cap.\n\n" +
                "Images are never shrunk, so either raise the cap, e.g.\n" +
                $"    RemoteDisplayCapture.Display \"{_folder}\" {_fps} {Math.Ceiling(estimatedBytes / 1073741824.0)}GB\n" +
                "or point the app at a smaller frame set.",
                "RemoteDisplayCapture.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        var frames = new BitmapSource?[_imagePaths.Count];
        int decoded = 0;
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        progressTimer.Tick += (_, _) =>
            LoadingText.Text = $"Decoding frames… {Volatile.Read(ref decoded)} / {frames.Length}";
        progressTimer.Start();

        await Task.Run(() =>
            Parallel.For(0, frames.Length, i =>
            {
                if (errors[i] is null)
                {
                    frames[i] = DecodeFrame(_imagePaths[i]);
                }
                Interlocked.Increment(ref decoded);
            }));

        progressTimer.Stop();
        LoadingText.Visibility = Visibility.Collapsed;

        if (frames.All(f => f is null) && errors.All(e => e is null))
        {
            MessageBox.Show($"None of the images in {_folder} could be decoded.",
                "RemoteDisplayCapture.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        _frames = frames;
        _frameErrors = errors;
        ShowStatus($"{_frames.Length} frames @ {_fps:0.###} fps");

        CompositionTarget.Rendering += OnRendering;
        _clock.Start();
    }

    /// <summary>
    /// Reads image headers only: flags frames larger than the monitor (they are never
    /// decoded) and estimates decoded memory for the rest at 4 bytes per pixel.
    /// Unreadable files count as zero and are skipped at decode time too.
    /// </summary>
    private long ScanFrames(string?[] errors)
    {
        long total = 0;
        Parallel.For(0, _imagePaths.Count, i =>
        {
            try
            {
                using var stream = File.OpenRead(_imagePaths[i]);
                var decoder = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > _screenPixelWidth || frame.PixelHeight > _screenPixelHeight)
                {
                    errors[i] = OversizeError(_imagePaths[i], frame.PixelWidth, frame.PixelHeight);
                }
                else
                {
                    Interlocked.Add(ref total, 4L * frame.PixelWidth * frame.PixelHeight);
                }
            }
            catch (Exception)
            {
                // Unreadable header — the frame will be skipped during decoding as well.
            }
        });
        return total;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1073741824.0:0.##} GB"
            : $"{bytes / 1048576.0:0.##} MB";

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per screen refresh; only act on new frames.
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = args.RenderingTime;
        }

        // Pure time-indexing skips frames whenever a render tick lands late across
        // a frame boundary. Instead, advance one frame per tick (no frame is ever
        // skipped) unless playback trails the clock by more than CatchUpFrames -
        // then jump to the time-accurate index. Rates above the render tick rate
        // stay permanently behind, so they get time-accurate skipping automatically.
        long timeIndex = (long)(CurrentSeconds * _fps);
        long rawIndex = Math.Min(timeIndex, _lastRawIndex + 1);
        if (timeIndex - rawIndex > CatchUpFrames)
        {
            rawIndex = timeIndex;
        }
        if (rawIndex < 0) rawIndex = 0;
        _lastRawIndex = rawIndex;

        if (_playOnce && rawIndex >= _frames.Length)
        {
            ShowTerminationScreen();
            return;
        }

        int index = (int)(rawIndex % _frames.Length);
        if (index == _lastShownFrame) return;
        _lastShownFrame = index;

        DisplayFrame(_frames[index], _frameErrors[index]);
    }

    /// <summary>
    /// Repositions the playback clock so that frame position <paramref name="framePosition"/>
    /// is "now", preserving continuity across speed changes, stepping, and pause/resume.
    /// </summary>
    private void RebaseClock(double framePosition)
    {
        _clockOffsetSeconds = framePosition / _fps;
        // Let the next tick land exactly on framePosition despite the no-skip clamp.
        _lastRawIndex = (long)framePosition - 1;
        _clock.Reset();
        if (!_paused) _clock.Start();
    }

    private void SetFlipbookPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _clockOffsetSeconds = CurrentSeconds;
            _clock.Reset();
        }
        else
        {
            _clock.Start();
        }
    }

    private void StepFrames(int delta)
    {
        double position = CurrentSeconds * _fps + delta;
        // Land mid-frame so float rounding can't flicker across the boundary.
        RebaseClock(Math.Floor(position) + 0.5);
        OnRendering(this, EventArgs.Empty);
    }

    private void ChangeSpeed(double factor)
    {
        double position = CurrentSeconds * _fps;
        _fps = Math.Clamp(_fps * factor, 0.1, MaxFps);
        RebaseClock(position);
        ShowStatus($"{_fps:0.###} fps");
    }

    // ---- Slideshow mode ----

    private async Task ShowNextAsync()
    {
        if (_playOnce && _currentIndex == _imagePaths.Count - 1)
        {
            ShowTerminationScreen();
            return;
        }
        await ShowImageAsync((_currentIndex + 1) % _imagePaths.Count);
    }

    private async Task ShowPreviousAsync() =>
        await ShowImageAsync((_currentIndex - 1 + _imagePaths.Count) % _imagePaths.Count);

    private async Task ShowImageAsync(int index)
    {
        BitmapSource? bitmap;
        if (_preloadTask is not null && _preloadIndex == index)
        {
            bitmap = await _preloadTask;
        }
        else
        {
            string path = _imagePaths[index];
            bitmap = await Task.Run(() => DecodeFrame(path));
        }

        _currentIndex = index;

        if (bitmap is not null)
        {
            if (bitmap.PixelWidth > _screenPixelWidth || bitmap.PixelHeight > _screenPixelHeight)
            {
                DisplayFrame(null, OversizeError(_imagePaths[index], bitmap.PixelWidth, bitmap.PixelHeight));
            }
            else
            {
                DisplayFrame(bitmap, null);
            }
        }

        // Decode the next image off the UI thread so the upcoming tick swaps instantly.
        _preloadIndex = (_currentIndex + 1) % _imagePaths.Count;
        string preloadPath = _imagePaths[_preloadIndex];
        _preloadTask = Task.Run(() => DecodeFrame(preloadPath));
    }

    // ---- Shared ----

    /// <summary>
    /// End of a single-pass run: stop playback and hold a clean, uniform
    /// termination-colour screen (the stop signal watched by RemoteDisplayCapture.Recorder).
    /// </summary>
    private void ShowTerminationScreen()
    {
        if (_terminated) return;
        _terminated = true;

        CompositionTarget.Rendering -= OnRendering;
        _timer.Stop();
        _clock.Reset();

        PhotoImage.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(_terminationColor);
    }

    private static BitmapSource? DecodeFrame(string path)
    {
        try
        {
            // Colour management is on by default: WIC applies an embedded ICC
            // profile during decode (BitmapCreateOptions.IgnoreColorProfile is the
            // opt-out), so tagged wide-gamut photos arrive correctly converted to
            // sRGB and untagged images arrive byte-exact. Do NOT add another
            // ColorConvertedBitmap on top — it double-converts.
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame.Freeze(); // allow use on the UI thread
            return frame;
        }
        catch (Exception)
        {
            // Corrupt or unreadable file — skip it rather than crash playback.
            return null;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Once the termination screen is up, keep it pristine: only Esc works.
        if (_terminated && e.Key != Key.Escape) return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;

            case Key.Space:
                if (_flipbookMode)
                {
                    SetFlipbookPaused(!_paused);
                }
                else
                {
                    _paused = !_paused;
                    if (_paused) _timer.Stop(); else _timer.Start();
                }
                ShowStatus(_paused ? "Paused" : "Playing");
                break;

            case Key.Right:
                if (_flipbookMode) StepFrames(1);
                else { _ = ShowNextAsync(); RestartTimerIfRunning(); }
                break;

            case Key.Left:
                if (_flipbookMode) StepFrames(-1);
                else { _ = ShowPreviousAsync(); RestartTimerIfRunning(); }
                break;

            case Key.Up:
                AdjustSpeed(2.0);
                break;

            case Key.Down:
                AdjustSpeed(0.5);
                break;
        }
    }

    private void AdjustSpeed(double factor)
    {
        if (_flipbookMode)
        {
            ChangeSpeed(factor);
        }
        else
        {
            // Slideshow mode never preloaded all frames, so cap it at the threshold.
            _fps = Math.Clamp(_fps * factor, 0.01, FlipbookThresholdFps);
            _timer.Interval = TimeSpan.FromSeconds(1.0 / _fps);
            ShowStatus($"{_fps:0.###} fps");
        }
    }

    private void RestartTimerIfRunning()
    {
        if (_paused) return;
        _timer.Stop();
        _timer.Start();
    }

    private async void ShowStatus(string message)
    {
        if (_terminated) return;
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        await Task.Delay(1500);
        if (StatusText.Text == message)
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }
}
