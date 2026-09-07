# CRT TV Simulator

Made by Oleksandr Aryku.

Play local videos, YouTube links and live screen capture inside an interactive 3D CRT television, with CRT effects, filtered video audio and a bouncing DVD fallback screen.

## Download And Run

**There is currently no packaged installer or ready-to-run GitHub release.** The download contains source code. Build it once using the steps below; Visual Studio is optional.

### Requirements

- Use a Windows 11 x64 PC with current graphics drivers. The project targets Windows SDK build 26100 (Windows 11 24H2); older Windows versions and ARM64 have not been verified.
- Install the **Windows x64 .NET 10 SDK** from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Choose the SDK, not just the runtime, to build the source. The SDK includes the runtime.
- Internet access is needed for the first build to download dependencies, and for YouTube playback.
- FFmpeg is required for local video and YouTube playback; see setup below.

### Get The App

1. Open the [repository](https://github.com/aryku0/CRT-TV-SIMULATOR) and choose **Code > Download ZIP**, or [download the source ZIP](https://github.com/aryku0/CRT-TV-SIMULATOR/archive/refs/heads/master.zip).
2. Right-click the ZIP and choose **Extract All**. Do not run from inside the ZIP.
3. Open the extracted folder containing `DvdLogoApp.slnx`, `DvdLogoApp` and `retro_90s_tv`. Keep these folders together.
4. Right-click an empty area in that folder and choose **Open in Terminal**.
5. Run:

```powershell
dotnet run --project .\DvdLogoApp\DvdLogoApp.csproj -c Release
```

The first launch takes longer while dependencies download and the app compiles. On later launches, double-click:

```text
DvdLogoApp\bin\Release\net10.0-windows10.0.26100.0\DvdLogoApp.exe
```

Keep the executable with its DLL files and `Assets` folder. Do not move only the executable; create a shortcut instead.

### Set Up FFmpeg

1. Visit the [official FFmpeg download page](https://ffmpeg.org/download.html) and follow its Windows builds link.
2. Download a Windows x64 binary build, not source code. A static build is simplest to copy.
3. Extract it and find `ffmpeg.exe`, normally inside its `bin` folder.
4. Start the app and open a video or YouTube link. If asked to locate FFmpeg, select that executable.

For automatic detection on future launches, put `ffmpeg.exe` beside `DvdLogoApp.exe`, or add FFmpeg's `bin` folder to Windows `PATH` and restart the app. For a shared build, keep its accompanying DLLs with the executable.

The app also checks `DVDLOGO_FFMPEG` (a full executable path), an `ffmpeg` subfolder beside the app, and the current user's WinGet package directory. FFmpeg is not bundled in this repository.

## Using The TV

1. Open **Options** with the top-right menu icon.
2. Press **Power** to turn on the TV.
3. Choose a source below. The status message under the buttons shows progress or errors.
4. Close Options with **X** to focus on the TV. Reopen it using the corner icon. A separate power button appears at the bottom-right while Options is hidden.

| Control | Action |
| --- | --- |
| Power | Turns the TV on or off. Powering off stops the active input; power on again and choose a new video or link. |
| Open video | Choose a video file on your PC, such as an MP4. Playback starts after loading. |
| YouTube link + play triangle | Paste a full URL, such as `https://www.youtube.com/watch?v=VIDEO_ID`, then press the triangle. |
| Capture screen | In the Windows picker, choose the window or display to show on the TV. |
| Stop input | Stops the source and returns to the DVD fallback screen. Disabled when no source is active. |
| Debug options | Opens advanced controls, separate from everyday playback. |

Local video and YouTube playback include an old-speaker-style audio filter. **Screen capture currently captures the picture only, not system audio.** Use content you have permission to access or capture; protected content may not work.

### Camera Controls

| Input | Action |
| --- | --- |
| Left-click and drag over the TV area | Orbit around the television |
| Mouse wheel | Zoom in or out |
| Arrow keys | Rotate left/right or tilt up/down |
| `+` and `-`, including numpad | Zoom in or out |
| Escape | Leave fullscreen |

Click the TV area before using camera shortcuts. **Options > Debug options** also contains Reset Camera and Fullscreen.

### Advanced Controls

Open **Options > Debug options** for camera sliders, CRT curve, scanlines, noise, glitch, vignette, Fresnel and glass settings. **Back to options** returns to the basic menu.

The `-10s` and `+10s` buttons seek backward or forward when the active video supports seeking. They are unavailable for screen capture.

Satisfaction is fixed at **0%**. Its original animation code is retained, but its control is hidden and disabled.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| `dotnet` not recognised | Install the .NET 10 SDK, then reopen the terminal. Check with `dotnet --info`. |
| SDK cannot target .NET 10 | Install a .NET 10 SDK; an older SDK or runtime alone cannot build this project. |
| Build files are in use | Close running copies of the app and build again. |
| Asked for `ffmpeg.exe` | Follow FFmpeg setup above. Select the decoder executable, not your video. |
| App or TV model fails to load | Keep the entire source/output folder together, including the model and assets. Update graphics drivers and note any startup error. |
| SharpEngine licence error | The project uses a third-party trial licence. If it expires or is rejected, report the exact message to the maintainer. Building successfully does not guarantee licence validity on every machine. |
| YouTube fails | Check the full URL and connection. Private, restricted or unavailable videos may fail; try a public video. YouTube service changes can require an app update. |
| Capture blank or frozen | Keep the source open and unminimised. Stop and select it again, or try the whole display. Protected content and some accelerated windows cannot be captured reliably. |
| No sound | Check Windows volume, output device and Volume Mixer. Confirm the video has audio. Screen capture does not include audio. |
| Stuttering | Close heavy background apps, try lower-resolution video and update graphics drivers. Playback targets 60 fps but depends on hardware and source; lower-frame-rate content does not gain new motion frames. |

For unresolved problems, open a [GitHub issue](https://github.com/aryku0/CRT-TV-SIMULATOR/issues) with your Windows version, input type, error message and reproduction steps. Do not share private videos or links.

## Build A Folder For Another PC

With the .NET 10 SDK installed, run from the project root:

```powershell
dotnet publish .\DvdLogoApp\DvdLogoApp.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\CRT-TV-Simulator
```

Copy or ZIP the **entire** `artifacts\CRT-TV-Simulator` folder. On another Windows x64 PC, extract it and run `DvdLogoApp.exe`. This includes the .NET runtime, but FFmpeg still needs installing or placing beside the executable. Graphics and third-party licence requirements still apply. Check FFmpeg's distribution licence before bundling it for others.

Visual Studio users can open `DvdLogoApp.slnx` in Visual Studio 2026 with .NET 10 support, select `DvdLogoApp` as the startup project and run it.

## Development And Credits

Built with C#, .NET 10, Avalonia UI, Ab4d.SharpEngine and its glTF importer, NAudio, FFmpeg and YoutubeExplode. Display capture uses Windows Graphics Capture; selected windows also use a PrintWindow capture path.

Skills demonstrated include desktop UI development, 3D model integration, animated CRT effects, audio/video decoding and synchronisation, input handling and automated regression testing.

Run playback-buffer and screen-border checks from the project root:

```powershell
dotnet run --project .\PlaybackRegression\PlaybackRegression.csproj -c Release
```

- **3D TV:** [Retro 90's TV by HiddenGhillieDhu](https://sketchfab.com/3d-models/retro-90s-tv-d1a52fcfd95d4901af3b6ae1359cc242), CC-BY-4.0. See `retro_90s_tv/license.txt`.
- **Jost font:** bundled for consistent typography. See `DvdLogoApp/Assets/Fonts/OFL-Jost.txt`.
- Third-party libraries and assets retain their own licence terms.
