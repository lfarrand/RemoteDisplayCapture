# RemoteDisplayCapture

RemoteDisplayCapture pairs two Windows applications that form a lossless display-and-capture pipeline:

- **RemoteDisplayCapture.Display** — a WPF viewer that plays a folder of images fullscreen at a configurable rate, from slow slideshows (one image every few seconds) up to 1000 fps flipbook playback. Images are always shown **pixel-perfect at 1:1** — never scaled, never compressed. Images smaller than the monitor are centred over a configurable border colour; images larger than the monitor are rejected with an on-screen error.
- **RemoteDisplayCapture.Recorder** — a console recorder built on DXGI desktop duplication that captures **every frame the monitor presents**, saving each one as a lossless image named `yyyyMMdd-N`. Recording stops automatically when the whole screen shows a configurable termination colour — the display app emits exactly that signal when run in `once` mode, so the pair operates hands-free end to end.

Both applications support multi-monitor systems (nominate which screen displays and which is captured), and the recorder can optionally capture the compositor's native HDR output as 32-bit float scRGB TIFFs.

## Supported platforms

| Requirement | Detail |
|---|---|
| OS | Windows 10 (1703 or later for HDR capture; 1803+ for the HDR colour-space warning) or Windows 11 |
| Runtime | .NET 10 (Windows Desktop) |
| Build | .NET 10 SDK — `dotnet build RemoteDisplayCapture.slnx` |
| Graphics | Any GPU with DXGI desktop duplication support (standard on desktop Windows) |

## RemoteDisplayCapture.Display

### Command line

```
RemoteDisplayCapture.Display <image-folder> [frames-per-second] [memory-cap] [once]
```

| Argument | Values | Default | Description |
|---|---|---|---|
| `image-folder` | path | required | Folder containing `.jpg`/`.jpeg`, `.png`, `.bmp`, `.gif` images, played in filename order |
| `frames-per-second` | > 0 to 1000 (decimals allowed) | `0.5` | Display rate. `0.2` = one image every 5 s; at or below 5 fps images stream one-ahead (slideshow mode); above 5 fps every frame is pre-decoded into RAM (flipbook mode) |
| `memory-cap` | e.g. `4`, `8GB`, `512MB` (bare number = GB) | `2GB` | Cap on flipbook pre-decode memory. Images are never shrunk to fit: if the frames need more, the app reports the required size and exits |
| `once` | literal `once`, any position | loop forever | Play a single pass, then hold the termination colour on screen — the recorder's stop signal |

### appsettings.json (next to the executable)

| Setting | Values | Default | Description |
|---|---|---|---|
| `BorderColor` | named colour or `#RRGGBB` | `#202020` | Colour surrounding images smaller than the monitor |
| `TerminationColor` | named colour or `#RRGGBB` | `#000000` | Colour held after a `once` pass finishes. Keep in sync with the recorder's `TerminationColor` |
| `DisplayScreen` | `0` = primary, `1..N` = detected screen | `0` | Which monitor shows the images (invalid values list the detected screens) |

### Keyboard controls

| Key | Action |
|---|---|
| `Esc` | Quit |
| `Space` | Pause / resume |
| `←` / `→` | Step one frame back / forward |
| `↑` / `↓` | Double / halve the playback rate |

### Display rules

- Image resolution equals the monitor's native resolution → fills the screen, one image pixel per screen pixel (correct even with Windows display scaling — the app is per-monitor DPI aware).
- Smaller than the monitor → shown 1:1, centred, surrounded by `BorderColor`.
- Larger than the monitor → not displayed; an on-screen message names the file and both resolutions.
- At rates within the render tick rate every frame is shown exactly once; above it, frames are skipped time-accurately so playback duration stays correct (a monitor cannot show more frames than it refreshes).

## RemoteDisplayCapture.Recorder

### Command line

```
RemoteDisplayCapture.Recorder <output-folder>
```

Captures start immediately and save as `yyyyMMdd-N.<ext>` (sequence continues if the folder already has captures for the day). A once-per-second status line reports `captured / saved / queued / missed` — `missed` comes from DXGI's accumulated-frames counter, so any capture shortfall is reported, never hidden. Stop with the termination colour or `Ctrl+C`.

### App.config

| Setting | Values | Default | Description |
|---|---|---|---|
| `TerminationColor` | named colour or `#RRGGBB` | `#000000` | Recording stops when the whole screen shows this colour. Match the display app's setting |
| `TerminationTolerance` | `0`–`255` | `30` | Per-channel slack when matching. With a non-zero value the frame must be uniform relative to itself *and* near the termination colour — this survives HDR tone mapping, which can shift colours by ~25 per channel. `0` = exact match only |
| `CaptureScreen` | `0` = primary, `1..N` = detected screen | `0` | Which monitor to record (the recorder lists detected screens at startup) |
| `OutputFormat` | `png`, `tiff`, `bmp` | `png` | Lossless capture format: PNG (compressed), TIFF (LZW), BMP (uncompressed, ~33 MB per 4K frame). GIF is not offered because it quantizes to 256 colours |
| `HdrCapture` | `true`, `false` | `false` | Capture the compositor's native HDR frames (FP16 scRGB or 10-bit PQ/BT.2020) and save 32-bit float linear scRGB TIFFs. Requires the captured screen to be HDR-composed. Keep `TerminationColor` black in this mode |

## Usage

Build everything, then start the recorder first and the display second:

```
dotnet build RemoteDisplayCapture.slnx

# Terminal 1 - record until the termination colour appears
RemoteDisplayCapture.Recorder C:\Captures

# Terminal 2 - play the folder once at 60 fps, then hold the stop signal
RemoteDisplayCapture.Display C:\Frames 60 once
```

The recorder saves every presented frame and exits by itself when the display finishes. For clean recordings, keep the recorder's console window on a different monitor than the one being captured.

### Colour fidelity notes

- On **SDR-composed** screens, captures are colour-exact.
- On **HDR or auto-colour-managed** screens, Windows tone-maps 8-bit captures, so pixel values shift (the recorder warns at startup). Either disable HDR on the capture screen for exact colours, or set `HdrCapture=true` to record the HDR composition itself, losslessly.
- HDR float TIFFs are large (tens of MB per frame) and some viewers (including WPF-based ones) preview them at 8-bit — the float data is intact and reads correctly in imaging tools such as ImageMagick or Python's `tifffile`.
- Images with embedded ICC profiles are colour-managed automatically during decode; untagged images pass through byte-exact.

## License

MIT — see [LICENSE](LICENSE).
