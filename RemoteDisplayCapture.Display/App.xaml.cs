using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace RemoteDisplayCapture.Display;

public partial class App : Application
{
    private const long DefaultMemoryCapBytes = 2L * 1024 * 1024 * 1024; // 2 GB

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "once" may appear anywhere; the remaining arguments stay positional.
        string[] positional = [.. e.Args.Where(a => !a.Equals("once", StringComparison.OrdinalIgnoreCase))];
        bool playOnce = positional.Length != e.Args.Length;

        string? folder = positional.Length > 0 ? positional[0] : null;
        double fps = 0.5; // default: one image every 2 seconds

        if (positional.Length > 1 &&
            !double.TryParse(positional[1], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out fps))
        {
            ShowUsage($"'{positional[1]}' is not a valid frames-per-second value.");
            return;
        }

        long memoryCap = DefaultMemoryCapBytes;
        if (positional.Length > 2)
        {
            if (TryParseMemoryCap(positional[2], out long parsed) && parsed > 0)
            {
                memoryCap = parsed;
            }
            else
            {
                ShowUsage($"'{positional[2]}' is not a valid memory cap. Use e.g. 2GB, 512MB, or 4 (gigabytes).");
                return;
            }
        }

        if (folder is null || !Directory.Exists(folder))
        {
            ShowUsage(folder is null
                ? "No image folder was specified."
                : $"Folder not found: {folder}");
            return;
        }

        if (fps <= 0 || fps > 1000)
        {
            ShowUsage("Frames per second must be greater than 0 and at most 1000.");
            return;
        }

        var screens = System.Windows.Forms.Screen.AllScreens;
        int screenIndex = LoadScreenIndexSetting("DisplayScreen");
        System.Windows.Forms.Screen? targetScreen = screenIndex switch
        {
            0 => System.Windows.Forms.Screen.PrimaryScreen ?? screens[0],
            _ when screenIndex <= screens.Length => screens[screenIndex - 1],
            _ => null,
        };
        if (targetScreen is null)
        {
            ShowUsage($"DisplayScreen {screenIndex} in appsettings.json is out of range.\n\n" +
                      $"Detected screens:\n{DescribeScreens(screens)}");
            return;
        }

        MainWindow = new MainWindow(folder, fps, memoryCap, playOnce,
            LoadColorSetting("BorderColor", Colors.Black),
            LoadColorSetting("TerminationColor", Colors.Black),
            targetScreen.Bounds);
        MainWindow.Show();
    }

    private static string DescribeScreens(System.Windows.Forms.Screen[] screens) =>
        string.Join("\n", screens.Select((s, i) =>
            $"  {i + 1}: {s.DeviceName} {s.Bounds.Width}x{s.Bounds.Height} at ({s.Bounds.X},{s.Bounds.Y})" +
            (s.Primary ? " [primary]" : "")));

    /// <summary>Reads a screen-number setting: 0 (or missing) means the primary screen.</summary>
    private static int LoadScreenIndexSetting(string key)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty(key, out var element) &&
                    element.TryGetInt32(out int value) && value >= 0)
                {
                    return value;
                }
            }
        }
        catch (Exception)
        {
            // Missing or malformed settings — fall back to the primary screen.
        }
        return 0;
    }

    private static Color LoadColorSetting(string key, Color fallback)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty(key, out var element) &&
                    element.GetString() is { } text &&
                    ColorConverter.ConvertFromString(text) is Color color)
                {
                    return color;
                }
            }
        }
        catch (Exception)
        {
            // Missing or malformed settings — fall back to the default.
        }
        return fallback;
    }

    /// <summary>Parses "4", "4GB", or "512MB" (case-insensitive; bare numbers mean gigabytes).</summary>
    private static bool TryParseMemoryCap(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();

        long multiplier = 1024L * 1024 * 1024;
        if (text.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2];
        }
        else if (text.EndsWith("MB", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024L * 1024;
            text = text[..^2];
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            return false;
        }

        bytes = (long)(value * multiplier);
        return true;
    }

    private void ShowUsage(string error)
    {
        MessageBox.Show(
            $"{error}\n\n" +
            "Usage: RemoteDisplayCapture.Display <image-folder> [frames-per-second] [memory-cap] [once]\n\n" +
            "Examples:\n" +
            "  RemoteDisplayCapture.Display C:\\Photos          (default: 0.5 fps, one image every 2s)\n" +
            "  RemoteDisplayCapture.Display C:\\Photos 0.2      (one image every 5s)\n" +
            "  RemoteDisplayCapture.Display C:\\Frames 500      (flipbook playback at 500 fps)\n" +
            "  RemoteDisplayCapture.Display C:\\Frames 500 8GB  (flipbook with an 8 GB pre-decode cap)\n" +
            "  RemoteDisplayCapture.Display C:\\Frames 10 once  (play a single pass, then hold the\n" +
            "                                    termination colour from appsettings.json)\n\n" +
            "Images are always shown pixel-perfect at full resolution. The memory cap\n" +
            "(default 2GB) guards flipbook pre-decoding; if the frames need more, the\n" +
            "app tells you the required size instead of shrinking them.\n\n" +
            "The border colour around images smaller than the monitor is set in\n" +
            "appsettings.json next to the executable.",
            "RemoteDisplayCapture.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown(1);
    }
}
