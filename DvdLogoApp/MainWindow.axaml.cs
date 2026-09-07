using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NAudio.Wave;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace DvdLogoApp;

public partial class MainWindow : Window
{
    private const string LogoFileName = "DVD_video_logo.png";
    private const string IntroSoundFileName = "edr-old-pc-monitor-switch-on-and-degaussing-8576.mp3";
    private const double FixedLogoSpeed = 262;
    private const int CornerRepeatWindow = 5;
    // Keeps the original DVD screensaver mode active until it is deliberately
    // disabled again for a future input-focused build.
    private static readonly bool EnableDvdScreensaver = true;

    private enum StageCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private static readonly string[] BounceSoundFileNames =
    [
        "bounce1.mp3",
        "bounce2.mp3",
        "bounce3.mp3"
    ];

    private static readonly Color[] LogoColors =
    [
        Colors.White,
        Color.FromRgb(255, 69, 91),
        Color.FromRgb(255, 207, 64),
        Color.FromRgb(78, 238, 148),
        Color.FromRgb(69, 202, 255),
        Color.FromRgb(191, 111, 255),
        Color.FromRgb(255, 126, 54)
    ];

    private readonly DispatcherTimer bounceTimer;
    private readonly DispatcherTimer satisfactionFadeTimer;
    private readonly DispatcherTimer externalInputTimer;
    private readonly Random random = new();
    private readonly List<AudioClip> bounceClips = new();
    private readonly List<StageCorner> recentCornerHits = [];
    private readonly Dictionary<Control, CancellationTokenSource> dynamicButtonAnimations = new();

    private CancellationTokenSource? satisfactionOpacityCancellation;
    private CancellationTokenSource? powerTransitionCancellation;
    private CancellationTokenSource? debugRailAnimationCancellation;
    private AudioClip? introClip;
    private Bitmap? logoTemplate;
    private byte[]? currentLogoPixels;
    private Vector velocity = new(280, 190);
    private DateTime lastFrameTime;
    private Point? currentCornerTarget;
    private StageCorner? lastCornerHit;
    private Color currentLogoColor = Colors.White;
    private int currentLogoPixelWidth;
    private int currentLogoPixelHeight;
    private int currentLogoPixelStride;
    private double logoX;
    private double logoY;
    private bool isDraggingSatisfaction;
    private bool isDraggingGlassFresnel;
    private bool isDraggingCornerRadius;
    private bool isOrbitingCamera;
    private Slider? activeCrtEffectSlider;
    private Point lastCameraPointerPosition;
    private bool isBouncing;
    private bool isPowerTransitioning;
    private bool isClosed;
    private bool isExternalInputActive;
    private FfmpegVideoSource? ffmpegVideoSource;
    private WindowsGraphicsCaptureSource? windowsCaptureSource;
    private string? selectedFfmpegPath;
    private readonly object externalFrameLock = new();
    private InputFrameEventArgs? latestExternalFrame;

    // Sets up the window, timers, and starting visual state.
    public MainWindow()
    {
        InitializeComponent();

        ScreenSurface.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        ScreenSurface.RenderTransform = new ScaleTransform(1.018, 1.012);
        SetupDynamicButton(PowerButton);
        SetupDynamicButton(ViewportPowerButton);
        SetupDynamicButton(ResetCameraButton);
        SetupDynamicButton(FullscreenButton);
        SetupDynamicButton(SkipBackwardButton);
        SetupDynamicButton(SkipForwardButton);
        DebugRail.RenderTransform = new TranslateTransform();
        SatisfactionSliderThumb.RenderTransform = new TranslateTransform();
        GlassFresnelSliderThumb.RenderTransform = new TranslateTransform();
        CornerRadiusSliderThumb.RenderTransform = new TranslateTransform();
        UpdateSatisfactionSliderVisual();
        UpdateGlassFresnelSliderVisual();
        UpdateCornerRadiusSliderVisual();
        UpdateCrtEffectValueTexts();
        UpdateCrtEffectSettings();
        UpdateScreenCornerRadius();
        UpdateKeepVisibleOption();
        SetDebugRailVisibility(true, animate: false);

        satisfactionFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        satisfactionFadeTimer.Tick += SatisfactionFadeTimer_Tick;

        bounceTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        bounceTimer.Tick += BounceTimer_Tick;

        externalInputTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // Keep the TV surface on a 60 Hz cadence. The latest available
            // frame is presented on each tick, so a slower source is repeated
            // smoothly while a faster source cannot queue work on the UI thread.
            Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0)
        };
        externalInputTimer.Tick += ExternalInputTimer_Tick;

        Loaded += Window_Loaded;
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        Closed += Window_Closed;
    }

    // Runs when the window opens and leaves the TV surface powered off until the power button is clicked.
    private void Window_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LoadLogo();
        LoadBounceSounds();
        PositionLogoForIntro();
        DvdLogoImage.Opacity = 0;
        PowerFlickerOverlay.Opacity = 0;
        PowerOffOverlay.Opacity = EnableDvdScreensaver ? 1 : 0;
        ShowDebugControlsNow();
        SetPowerStartHintVisible(EnableDvdScreensaver);
        SetPowerControlTooltip(EnableDvdScreensaver ? "Power on" : "Power off");
        InputStatusText.Text = EnableDvdScreensaver ? "DVD screensaver" : "TV ready for input";
        UpdateFullscreenButtonText();
        UpdateTvDebugCamera();
        PushScreenToTvTexture();
    }

    // Stops timers and releases audio/image resources when the window closes.
    private void Window_Closed(object? sender, EventArgs e)
    {
        isClosed = true;
        powerTransitionCancellation?.Cancel();
        bounceTimer.Stop();
        externalInputTimer.Stop();
        satisfactionFadeTimer.Stop();
        satisfactionOpacityCancellation?.Cancel();
        debugRailAnimationCancellation?.Cancel();
        StopExternalInputSynchronously();
        introClip?.Dispose();
        CloseBounceClips();
        logoTemplate?.Dispose();
        CancelDynamicButtonAnimations();
    }

    // Loads the DVD logo from the bundled Avalonia app assets.
    private void LoadLogo()
    {
        var logoUri = new Uri($"avares://DvdLogoApp/Assets/{LogoFileName}");

        using var stream = AssetLoader.Open(logoUri);
        logoTemplate = new Bitmap(stream);
        ApplyLogoColor(Colors.White);
    }

    // Plays the CRT switch-on sound if the MP3 is present.
    private void PlayIntroSound()
    {
        var audioPath = GetOutputAssetPath(IntroSoundFileName);

        if (!File.Exists(audioPath))
        {
            return;
        }

        introClip?.Dispose();
        introClip = new AudioClip(audioPath, 0.9f);
        introClip.PlayFromStart();
    }

    // Loads the bounce sound effects so one can be picked randomly on each bounce.
    private void LoadBounceSounds()
    {
        CloseBounceClips();

        foreach (var fileName in BounceSoundFileNames)
        {
            var audioPath = GetOutputAssetPath(fileName);

            if (!File.Exists(audioPath))
            {
                continue;
            }

            bounceClips.Add(new AudioClip(audioPath, 0.62f));
        }
    }

    // Places the logo in its pre-start intro position.
    private void PositionLogoForIntro()
    {
        logoX = GetCenteredLogoX();
        logoY = GetIntroLogoY();
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);
        PushScreenToTvTexture();
    }

    // Power is independent of whether the current source is the logo or video.
    private async void PowerButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (isPowerTransitioning)
        {
            return;
        }

        if (!EnableDvdScreensaver)
        {
            if (PowerOffOverlay.Opacity >= 0.99)
            {
                PowerOffOverlay.Opacity = 0;
                SetPowerControlTooltip("Power off");
                InputStatusText.Text = "TV ready for input";
                PushScreenToTvTexture();
            }
            else
            {
                await StopExternalInputAsync();
                TvModelStage.ClearExternalVideoFrame();
                PowerOffOverlay.Opacity = 1;
                SetPowerControlTooltip("Power on");
                InputStatusText.Text = "TV powered off";
                PushScreenToTvTexture();
            }

            return;
        }

        if (PowerOffOverlay.Opacity < 0.99)
        {
            await PowerOff();
            return;
        }

        await PowerOn();
    }

    // Switches between the normal window and fullscreen when the fullscreen button is clicked.
    private void FullscreenButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    // Opens a video file and sends its decoded frames into the TV screen.
    private async void OpenVideoButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeExternalInput())
        {
            return;
        }

        var ffmpegPath = selectedFfmpegPath ?? FindFfmpegPath();
        if (ffmpegPath is null)
        {
            var ffmpegFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Locate ffmpeg.exe",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("FFmpeg executable")
                    {
                        Patterns = ["ffmpeg.exe"]
                    }
                ]
            });

            ffmpegPath = ffmpegFiles.FirstOrDefault()?.Path.LocalPath;
            if (ffmpegPath is null)
            {
                InputStatusText.Text = "Video cancelled: ffmpeg.exe is required.";
                return;
            }

            selectedFfmpegPath = ffmpegPath;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a video for the TV",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video files")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.wmv", "*.m4v"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        var path = file.Path.LocalPath;
        await StopExternalInputAsync();

        try
        {
            ffmpegVideoSource = new FfmpegVideoSource();
            ffmpegVideoSource.FrameReady += ExternalInput_FrameReady;
            await ffmpegVideoSource.StartAsync(ffmpegPath, path);
            ActivateExternalInput($"Video: {file.Name}");
        }
        catch (Exception exception)
        {
            await StopExternalInputAsync();
            InputStatusText.Text = $"Video could not start: {exception.Message}";
        }
    }

    // Resolves a YouTube link to a playable stream and sends it through the
    // same FFmpeg, CRT shader, and vintage speaker path as local videos.
    private async void PlayYoutubeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeExternalInput())
        {
            return;
        }

        var url = YoutubeUrlTextBox.Text?.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            InputStatusText.Text = "Paste a valid YouTube link first.";
            return;
        }

        var ffmpegPath = selectedFfmpegPath ?? FindFfmpegPath();
        if (ffmpegPath is null)
        {
            var ffmpegFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Locate ffmpeg.exe",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("FFmpeg executable")
                    {
                        Patterns = ["ffmpeg.exe"]
                    }
                ]
            });

            ffmpegPath = ffmpegFiles.FirstOrDefault()?.Path.LocalPath;
            if (ffmpegPath is null)
            {
                InputStatusText.Text = "YouTube cancelled: ffmpeg.exe is required.";
                return;
            }

            selectedFfmpegPath = ffmpegPath;
        }

        PlayYoutubeButton.IsEnabled = false;
        InputStatusText.Text = "Resolving YouTube link...";

        try
        {
            var youtube = new YoutubeClient();
            var video = await youtube.Videos.GetAsync(url);
            var manifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);
            var muxedStreams = manifest.GetMuxedStreams()
                .Where(candidate => candidate.Container == Container.Mp4)
                .ToList();

            await StopExternalInputAsync();
            ffmpegVideoSource = new FfmpegVideoSource();
            ffmpegVideoSource.FrameReady += ExternalInput_FrameReady;

            if (muxedStreams.Count > 0)
            {
                var stream = muxedStreams.GetWithHighestVideoQuality();
                await ffmpegVideoSource.StartStreamAsync(ffmpegPath, stream.Url);
            }
            else
            {
                var videoStreams = manifest.GetVideoOnlyStreams().ToList();
                var videoStreamsAt720p = videoStreams
                    .Where(candidate => candidate.VideoQuality.MaxHeight <= 720)
                    .ToList();
                var videoStream = (videoStreamsAt720p.Count > 0 ? videoStreamsAt720p : videoStreams)
                    .OrderByDescending(candidate => candidate.VideoQuality.MaxHeight)
                    .FirstOrDefault();
                var audioStream = manifest.GetAudioOnlyStreams()
                    .OrderByDescending(candidate => candidate.Bitrate.BitsPerSecond)
                    .FirstOrDefault();

                if (videoStream is null || audioStream is null)
                {
                    throw new InvalidOperationException("YouTube did not provide compatible video and audio streams.");
                }

                await ffmpegVideoSource.StartStreamPairAsync(ffmpegPath, videoStream.Url, audioStream.Url);
            }

            ActivateExternalInput($"YouTube: {video.Title}");
        }
        catch (Exception exception)
        {
            await StopExternalInputAsync();
            InputStatusText.Text = $"YouTube could not start: {exception.Message}";
        }
        finally
        {
            PlayYoutubeButton.IsEnabled = true;
        }
    }

    // Opens the secure Windows picker for a window or display and starts WGC.
    private async void CaptureScreenButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeExternalInput())
        {
            return;
        }

        var ownerHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (ownerHandle == IntPtr.Zero)
        {
            InputStatusText.Text = "The Windows capture picker could not find this window.";
            return;
        }

        await StopExternalInputAsync();

        try
        {
            windowsCaptureSource = new WindowsGraphicsCaptureSource();
            windowsCaptureSource.FrameReady += ExternalInput_FrameReady;
            windowsCaptureSource.CaptureFailed += CaptureSource_Failed;

            if (!await windowsCaptureSource.PickAndStartAsync(ownerHandle))
            {
                await StopExternalInputAsync();
                return;
            }

            ActivateExternalInput("Live Windows capture");
        }
        catch (Exception exception)
        {
            await StopExternalInputAsync();
            InputStatusText.Text = $"Screen capture could not start: {exception.Message}";
        }
    }

    // Stops file playback or WGC and returns the TV to its built-in screensaver.
    private async void StopInputButton_Click(object? sender, RoutedEventArgs e)
    {
        await StopExternalInputAsync();
        TvModelStage.ClearExternalVideoFrame();
        InputStatusText.Text = "DVD screensaver";

        if (EnableDvdScreensaver && PowerOffOverlay.Opacity < 0.99 && !isPowerTransitioning)
        {
            StartBouncing();
        }
        else
        {
            PushScreenToTvTexture();
        }
    }

    private async void SkipBackwardButton_Click(object? sender, RoutedEventArgs e)
    {
        await SkipExternalInputAsync(TimeSpan.FromSeconds(-10));
    }

    private async void SkipForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        await SkipExternalInputAsync(TimeSpan.FromSeconds(10));
    }

    private async Task SkipExternalInputAsync(TimeSpan amount)
    {
        if (ffmpegVideoSource is null || !ffmpegVideoSource.CanSeek)
        {
            return;
        }

        SkipBackwardButton.IsEnabled = false;
        SkipForwardButton.IsEnabled = false;
        InputStatusText.Text = amount < TimeSpan.Zero ? "Seeking backward..." : "Seeking forward...";

        InputFrameEventArgs? pendingFrame;
        lock (externalFrameLock)
        {
            pendingFrame = latestExternalFrame;
            latestExternalFrame = null;
        }
        pendingFrame?.Release();

        try
        {
            await ffmpegVideoSource.SkipAsync(amount);
            InputStatusText.Text = "Video input";
        }
        catch (Exception exception)
        {
            InputStatusText.Text = $"Seek failed: {exception.Message}";
        }
        finally
        {
            var canSeek = ffmpegVideoSource?.CanSeek == true;
            SkipBackwardButton.IsEnabled = canSeek;
            SkipForwardButton.IsEnabled = canSeek;
        }
    }

    private bool CanChangeExternalInput()
    {
        if (isPowerTransitioning)
        {
            return false;
        }

        if (PowerOffOverlay.Opacity >= 0.99)
        {
            InputStatusText.Text = "Power on the TV first.";
            return false;
        }

        return true;
    }

    private void ActivateExternalInput(string status)
    {
        isExternalInputActive = true;
        bounceTimer.Stop();
        isBouncing = false;
        currentCornerTarget = null;
        DvdLogoImage.Opacity = 0;
        PowerFlickerOverlay.Opacity = 0;
        PowerOffOverlay.Opacity = 0;
        InputStatusText.Text = status;
        StopInputButton.IsEnabled = true;
        SkipBackwardButton.IsEnabled = ffmpegVideoSource?.CanSeek == true;
        SkipForwardButton.IsEnabled = ffmpegVideoSource?.CanSeek == true;
        externalInputTimer.Start();
        PushScreenToTvTexture();
    }

    private void ExternalInput_FrameReady(object? sender, InputFrameEventArgs e)
    {
        if (isClosed)
        {
            e.Release();
            return;
        }

        InputFrameEventArgs? replacedFrame;
        lock (externalFrameLock)
        {
            replacedFrame = latestExternalFrame;
            latestExternalFrame = e;
        }
        replacedFrame?.Release();
    }

    private void ExternalInputTimer_Tick(object? sender, EventArgs e)
    {
        if (isClosed || !isExternalInputActive)
        {
            return;
        }

        InputFrameEventArgs? frame;
        lock (externalFrameLock)
        {
            frame = latestExternalFrame;
            latestExternalFrame = null;
        }

        if (frame is null)
        {
            return;
        }

        try
        {
            TvModelStage.SetExternalVideoFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride);
            if (frame.Sequence > 0)
            {
                InputStatusText.Text = $"Live Windows capture • frame {frame.Sequence}";
            }
            PushScreenToTvTexture();
            ffmpegVideoSource?.StartSynchronizedAudio();
        }
        finally
        {
            frame.Release();
        }
    }

    private void CaptureSource_Failed(object? sender, CaptureErrorEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!isClosed)
            {
                InputStatusText.Text = $"Capture frame error: {e.Exception.Message}";
            }
        });
    }

    private async Task StopExternalInputAsync()
    {
        isExternalInputActive = false;
        externalInputTimer.Stop();
        InputFrameEventArgs? pendingFrame;
        lock (externalFrameLock)
        {
            pendingFrame = latestExternalFrame;
            latestExternalFrame = null;
        }
        pendingFrame?.Release();

        if (ffmpegVideoSource is not null)
        {
            ffmpegVideoSource.FrameReady -= ExternalInput_FrameReady;
            await ffmpegVideoSource.DisposeAsync();
            ffmpegVideoSource = null;
        }

        if (windowsCaptureSource is not null)
        {
            windowsCaptureSource.FrameReady -= ExternalInput_FrameReady;
            windowsCaptureSource.CaptureFailed -= CaptureSource_Failed;
            windowsCaptureSource.Dispose();
            windowsCaptureSource = null;
        }

        if (!isClosed && StopInputButton is not null)
        {
            StopInputButton.IsEnabled = false;
            SkipBackwardButton.IsEnabled = false;
            SkipForwardButton.IsEnabled = false;
        }
    }

    private void StopExternalInputSynchronously()
    {
        isExternalInputActive = false;
        externalInputTimer.Stop();
        InputFrameEventArgs? pendingFrame;
        lock (externalFrameLock)
        {
            pendingFrame = latestExternalFrame;
            latestExternalFrame = null;
        }
        pendingFrame?.Release();
        ffmpegVideoSource?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ffmpegVideoSource = null;
        SkipBackwardButton.IsEnabled = false;
        SkipForwardButton.IsEnabled = false;
        windowsCaptureSource?.Dispose();
        windowsCaptureSource = null;
    }

    private static string? FindFfmpegPath()
    {
        var candidates = new List<string>
        {
            Environment.GetEnvironmentVariable("DVDLOGO_FFMPEG") ?? string.Empty,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe")
        };

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), "ffmpeg.exe")));

        var wingetPackages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Packages");
        if (Directory.Exists(wingetPackages))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(wingetPackages, "ffmpeg.exe", SearchOption.AllDirectories));
            }
            catch (UnauthorizedAccessException)
            {
                // A package manager may protect part of its cache; PATH and
                // the app-local locations remain valid discovery options.
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    // Keeps the debug camera sliders synced with the 3D TV stage.
    private void CameraSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateTvDebugCamera();
    }

    // Restores the front-on test camera.
    private void ResetCameraButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ResetDebugCamera();
    }

    // Toggles the debug rail without taking space away from the TV when it is hidden.
    private void DebugMenuButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowOptionsView(false);
        SetDebugRailVisibility(DebugRail is not null && !DebugRail.IsVisible);
    }

    private void OpenDebugOptions_Click(object? sender, RoutedEventArgs e) => ShowOptionsView(true);

    private void BackToOptions_Click(object? sender, RoutedEventArgs e) => ShowOptionsView(false);

    private void ShowOptionsView(bool debug)
    {
        BasicOptionsPanel.IsVisible = !debug;
        DebugOptionsPanel.IsVisible = debug;
        OptionsTitle.Text = debug ? "Debug options" : "Options";
        OptionsScrollViewer.Offset = new Vector(0, 0);
    }

    // Closes the debug rail from its own header button.
    private void CloseDebugMenuButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetDebugRailVisibility(false);
    }

    private void SetDebugRailVisibility(bool isVisible, bool animate = true)
    {
        if (DebugRail is null || AppLayout.ColumnDefinitions.Count < 2)
        {
            return;
        }

        debugRailAnimationCancellation?.Cancel();

        if (!animate)
        {
            ApplyDebugRailVisibility(isVisible);
            return;
        }

        var cancellation = new CancellationTokenSource();
        debugRailAnimationCancellation = cancellation;
        _ = AnimateDebugRailAsync(isVisible, cancellation);
    }

    private async Task AnimateDebugRailAsync(bool isVisible, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var transform = DebugRail.RenderTransform as TranslateTransform;
        var startWidth = AppLayout.ColumnDefinitions[1].Width.Value;
        var targetWidth = isVisible ? 280 : 0;
        var startOpacity = DebugRail.Opacity;
        var targetOpacity = isVisible ? 1 : 0;
        var startOffset = transform?.X ?? 0;
        var targetOffset = isVisible ? 0 : 28;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(260);

        if (isVisible)
        {
            DebugRail.IsVisible = true;
            DebugMenuButton.IsVisible = false;
            ViewportPowerButton.IsVisible = false;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var progress = Clamp01((DateTime.UtcNow - startTime).TotalMilliseconds / duration.TotalMilliseconds);
                var eased = EaseOutQuad(progress);
                AppLayout.ColumnDefinitions[1].Width = new GridLength(Lerp(startWidth, targetWidth, eased));
                DebugRail.Opacity = Lerp(startOpacity, targetOpacity, eased);

                if (transform is not null)
                {
                    transform.X = Lerp(startOffset, targetOffset, eased);
                }

                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(16, cancellationToken);
            }

            ApplyDebugRailVisibility(isVisible);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(debugRailAnimationCancellation, cancellation))
            {
                debugRailAnimationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ApplyDebugRailVisibility(bool isVisible)
    {
        AppLayout.ColumnDefinitions[1].Width = new GridLength(isVisible ? 280 : 0);
        DebugRail.Opacity = isVisible ? 1 : 0;
        DebugRail.IsVisible = isVisible;
        DebugMenuButton.IsVisible = !isVisible;
        ViewportPowerButton.IsVisible = !isVisible;

        if (DebugRail.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
        }

        ToolTip.SetTip(DebugMenuButton, "Show options");
    }

    private void SetPowerControlEnabled(bool isEnabled)
    {
        PowerButton.IsEnabled = isEnabled;
        ViewportPowerButton.IsEnabled = isEnabled;
    }

    private void SetPowerControlTooltip(string tooltip)
    {
        ToolTip.SetTip(PowerButton, tooltip);
        ToolTip.SetTip(ViewportPowerButton, tooltip);
    }

    // Handles keyboard camera controls even when a child control has focus.
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ExitFullscreen();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                AdjustCamera(headingDelta: -3);
                break;
            case Key.Right:
                AdjustCamera(headingDelta: 3);
                break;
            case Key.Up:
                AdjustCamera(attitudeDelta: 2);
                break;
            case Key.Down:
                AdjustCamera(attitudeDelta: -2);
                break;
            case Key.Add:
            case Key.OemPlus:
                AdjustCamera(distanceDelta: -0.2);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                AdjustCamera(distanceDelta: 0.2);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // Begins camera orbiting when the user drags anywhere over the 3D viewport.
    private void TvViewportHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TvViewportHost).Properties.IsLeftButtonPressed)
        {
            return;
        }

        TvViewportHost.Focus();
        isOrbitingCamera = true;
        lastCameraPointerPosition = e.GetPosition(TvViewportHost);
        e.Pointer.Capture(TvViewportHost);
        e.Handled = true;
    }

    // Orbits the camera as the pointer is dragged across the 3D viewport.
    private void TvViewportHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isOrbitingCamera)
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(TvViewportHost);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            EndCameraOrbit(e.Pointer);
            return;
        }

        var position = e.GetPosition(TvViewportHost);
        var delta = position - lastCameraPointerPosition;
        lastCameraPointerPosition = position;
        AdjustCamera(headingDelta: delta.X * 0.32, attitudeDelta: -delta.Y * 0.24);
        e.Handled = true;
    }

    // Finishes the current camera orbit when the primary pointer button is released.
    private void TvViewportHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isOrbitingCamera)
        {
            return;
        }

        EndCameraOrbit(e.Pointer);
        e.Handled = true;
    }

    // Avoids leaving the camera in drag mode when capture is handed to another control.
    private void TvViewportHost_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isOrbitingCamera = false;
    }

    // Uses the mouse wheel as a fine zoom control over the 3D viewport.
    private void TvViewportHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        TvViewportHost.Focus();
        AdjustCamera(distanceDelta: -e.Delta.Y * 0.2);
        e.Handled = true;
    }

    // Updates the camera slider values while preserving their tested ranges.
    private void AdjustCamera(double headingDelta = 0, double attitudeDelta = 0, double distanceDelta = 0)
    {
        if (CameraHeadingSlider is null || CameraAttitudeSlider is null || CameraDistanceSlider is null)
        {
            return;
        }

        CameraHeadingSlider.Value = Math.Clamp(
            CameraHeadingSlider.Value + headingDelta,
            CameraHeadingSlider.Minimum,
            CameraHeadingSlider.Maximum);
        CameraAttitudeSlider.Value = Math.Clamp(
            CameraAttitudeSlider.Value + attitudeDelta,
            CameraAttitudeSlider.Minimum,
            CameraAttitudeSlider.Maximum);
        CameraDistanceSlider.Value = Math.Clamp(
            CameraDistanceSlider.Value + distanceDelta,
            CameraDistanceSlider.Minimum,
            CameraDistanceSlider.Maximum);
    }

    private void EndCameraOrbit(IPointer pointer)
    {
        isOrbitingCamera = false;
        pointer.Capture(null);
    }

    // Enters fullscreen if windowed, or returns to windowed mode if already fullscreen.
    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            ExitFullscreen();
            return;
        }

        WindowState = WindowState.FullScreen;
        UpdateFullscreenButtonText();
    }

    // Returns the app to normal windowed mode.
    private void ExitFullscreen()
    {
        WindowState = WindowState.Normal;
        UpdateFullscreenButtonText();
    }

    // Keeps the fullscreen button tooltip in sync with the current window mode.
    private void UpdateFullscreenButtonText()
    {
        ToolTip.SetTip(
            FullscreenButton,
            WindowState == WindowState.FullScreen ? "Exit fullscreen" : "Fullscreen");
    }

    // Powers on the TV surface and immediately starts the live bouncing logo.
    private async Task PowerOn()
    {
        powerTransitionCancellation?.Dispose();
        var transition = new CancellationTokenSource();
        powerTransitionCancellation = transition;
        var cancellationToken = transition.Token;

        isPowerTransitioning = true;
        SetPowerControlEnabled(false);
        ResetCameraButton.IsEnabled = false;
        FullscreenButton.IsEnabled = false;

        try
        {
            SetPowerStartHintVisible(false);
            PositionLogoForIntro();
            PlayIntroSound();
            await RunPowerOnFlicker(cancellationToken);
            StartBouncing(revealImmediately: false);
            await RunPowerOnFade(cancellationToken);
            InputStatusText.Text = "TV ready for input";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!isClosed)
            {
                SetPowerControlEnabled(true);
                ResetCameraButton.IsEnabled = true;
                FullscreenButton.IsEnabled = true;
                isPowerTransitioning = false;
            }

            if (ReferenceEquals(powerTransitionCancellation, transition))
            {
                transition.Dispose();
                powerTransitionCancellation = null;
            }
        }
    }

    // Switches into the live bouncing logo simulation.
    private void StartBouncing(bool revealImmediately = true)
    {
        if (revealImmediately)
        {
            PowerFlickerOverlay.Opacity = 0;
            PowerOffOverlay.Opacity = 0;
            DvdLogoImage.Opacity = 1;
            PushScreenToTvTexture();
        }

        logoX = GetValidCanvasValue(Canvas.GetLeft(DvdLogoImage), GetCenteredLogoX());
        logoY = GetValidCanvasValue(Canvas.GetTop(DvdLogoImage), GetIntroLogoY());
        ClampLogoPosition();

        velocity = CreateInitialVelocity();
        currentCornerTarget = null;
        lastCornerHit = null;
        recentCornerHits.Clear();
        ApplySatisfactionBounce();

        isBouncing = true;
        SetPowerControlTooltip("Power off");
        lastFrameTime = DateTime.UtcNow;
        bounceTimer.Start();
    }

    // Powers down the TV surface with a flicker and leaves it off.
    private async Task PowerOff()
    {
        powerTransitionCancellation?.Dispose();
        var transition = new CancellationTokenSource();
        powerTransitionCancellation = transition;
        var cancellationToken = transition.Token;

        isPowerTransitioning = true;
        SetPowerControlEnabled(false);
        ResetCameraButton.IsEnabled = false;
        FullscreenButton.IsEnabled = false;
        satisfactionFadeTimer.Stop();
        satisfactionOpacityCancellation?.Cancel();
        bounceTimer.Stop();
        isBouncing = false;
        await StopExternalInputAsync();
        TvModelStage.ClearExternalVideoFrame();
        InputStatusText.Text = "DVD screensaver";
        currentCornerTarget = null;
        lastCornerHit = null;
        recentCornerHits.Clear();

        try
        {
            SetPowerStartHintVisible(false);
            await RunPowerFlicker(cancellationToken);
            await RunPowerOffFade(cancellationToken);

            PositionLogoForIntro();
            DvdLogoImage.Opacity = 0;
            ShowDebugControlsNow();
            PowerOffOverlay.Opacity = 1;
            SetPowerStartHintVisible(true);
            SetPowerControlTooltip("Power on");
            InputStatusText.Text = "TV powered off";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!isClosed)
            {
                PowerFlickerOverlay.Opacity = 0;
                SetPowerControlEnabled(true);
                ResetCameraButton.IsEnabled = true;
                FullscreenButton.IsEnabled = true;
                isPowerTransitioning = false;
            }

            if (ReferenceEquals(powerTransitionCancellation, transition))
            {
                transition.Dispose();
                powerTransitionCancellation = null;
            }
        }
    }

    // Gives the TV a small wake flicker before the logo begins moving.
    private async Task RunPowerOnFlicker(CancellationToken cancellationToken)
    {
        PowerOffOverlay.Opacity = 1;
        PowerFlickerOverlay.Opacity = 0;
        DvdLogoImage.Opacity = 0;

        var flickerSteps = new[]
        {
            (White: 0.0, Black: 1.0, Delay: 70),
            (White: 0.48, Black: 0.35, Delay: 32),
            (White: 0.0, Black: 0.68, Delay: 38),
            (White: 0.26, Black: 0.18, Delay: 26),
            (White: 0.0, Black: 0.0, Delay: 35)
        };

        foreach (var step in flickerSteps)
        {
            PowerFlickerOverlay.Opacity = step.White;
            PowerOffOverlay.Opacity = step.Black;
            PushScreenToTvTexture();
            await Task.Delay(step.Delay, cancellationToken);
        }

        PowerFlickerOverlay.Opacity = 0;
        PowerOffOverlay.Opacity = 0.58;
        PushScreenToTvTexture();
    }

    // Slowly brings the live screen into view while the logo begins moving.
    private async Task RunPowerOnFade(CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(1050);
        var startBlack = PowerOffOverlay.Opacity;
        var startLogo = DvdLogoImage.Opacity;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progress = Clamp01((DateTime.UtcNow - startTime).TotalMilliseconds / duration.TotalMilliseconds);
            var eased = EaseOutQuad(progress);

            PowerOffOverlay.Opacity = Lerp(startBlack, 0, eased);
            DvdLogoImage.Opacity = Lerp(startLogo, 1, eased);
            PushScreenToTvTexture();

            if (progress >= 1)
            {
                break;
            }

            await Task.Delay(16, cancellationToken);
        }

        PowerOffOverlay.Opacity = 0;
        DvdLogoImage.Opacity = 1;
        PushScreenToTvTexture();
    }

    // Creates the quick flash feel of an old TV turning off.
    private async Task RunPowerFlicker(CancellationToken cancellationToken)
    {
        var flickerSteps = new[]
        {
            (White: 0.88, Black: 0.0, Delay: 34),
            (White: 0.0, Black: 0.4, Delay: 42),
            (White: 0.48, Black: 0.14, Delay: 28),
            (White: 0.0, Black: 0.46, Delay: 48),
            (White: 0.22, Black: 0.24, Delay: 24),
            (White: 0.0, Black: 0.22, Delay: 70)
        };

        foreach (var step in flickerSteps)
        {
            PowerFlickerOverlay.Opacity = step.White;
            PowerOffOverlay.Opacity = step.Black;
            DvdLogoImage.Opacity = Math.Max(0, 1 - step.Black);
            PushScreenToTvTexture();
            await Task.Delay(step.Delay, cancellationToken);
        }

        PowerFlickerOverlay.Opacity = 0;
        PushScreenToTvTexture();
    }

    // Slowly fades the live screen into the off state.
    private async Task RunPowerOffFade(CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(920);
        var startBlack = PowerOffOverlay.Opacity;
        var startLogo = DvdLogoImage.Opacity;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progress = Clamp01((DateTime.UtcNow - startTime).TotalMilliseconds / duration.TotalMilliseconds);
            var eased = EaseOutQuad(progress);

            PowerOffOverlay.Opacity = Lerp(startBlack, 1, eased);
            DvdLogoImage.Opacity = Lerp(startLogo, 0, eased);
            PushScreenToTvTexture();

            if (progress >= 1)
            {
                break;
            }

            await Task.Delay(16, cancellationToken);
        }

        PowerOffOverlay.Opacity = 1;
        DvdLogoImage.Opacity = 0;
        PushScreenToTvTexture();
    }

    // Gives the round console buttons a small physical lift/dip response.
    private static void SetupDynamicButton(Control button)
    {
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(1, 1));
        transform.Children.Add(new TranslateTransform());

        button.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        button.RenderTransform = transform;
    }

    private void DynamicButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control button && button.IsEnabled)
        {
            AnimateDynamicButton(button, 1.015, -0.5);
        }
    }

    private void DynamicButton_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control button)
        {
            AnimateDynamicButton(button, 1, 0);
        }
    }

    private void DynamicButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control button && button.IsEnabled)
        {
            AnimateDynamicButton(button, 0.94, 3);
        }
    }

    private void DynamicButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control button && button.IsEnabled)
        {
            AnimateDynamicButton(button, button.IsPointerOver ? 1.015 : 1, button.IsPointerOver ? -0.5 : 0);
        }
    }

    private async void AnimateDynamicButton(Control button, double targetScale, double targetY)
    {
        if (button.RenderTransform is not TransformGroup transform
            || transform.Children.Count < 2
            || transform.Children[0] is not ScaleTransform scale
            || transform.Children[1] is not TranslateTransform translate)
        {
            return;
        }

        if (dynamicButtonAnimations.TryGetValue(button, out var previousAnimation))
        {
            previousAnimation.Cancel();
        }

        var animation = new CancellationTokenSource();
        var animationToken = animation.Token;
        dynamicButtonAnimations[button] = animation;
        var startScale = scale.ScaleX;
        var startY = translate.Y;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(135);
        var completed = false;

        try
        {
            while (true)
            {
                animationToken.ThrowIfCancellationRequested();

                var progress = Clamp01((DateTime.UtcNow - startTime).TotalMilliseconds / duration.TotalMilliseconds);
                var eased = EaseOutQuad(progress);

                scale.ScaleX = Lerp(startScale, targetScale, eased);
                scale.ScaleY = Lerp(startScale, targetScale, eased);
                translate.Y = Lerp(startY, targetY, eased);

                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(16, animationToken);
            }

            completed = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (dynamicButtonAnimations.TryGetValue(button, out var currentAnimation)
                && currentAnimation == animation)
            {
                dynamicButtonAnimations.Remove(button);
            }

            animation.Dispose();
        }

        if (completed)
        {
            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;
            translate.Y = targetY;
        }
    }

    private void CancelDynamicButtonAnimations()
    {
        foreach (var animation in dynamicButtonAnimations.Values)
        {
            animation.Cancel();
        }

        dynamicButtonAnimations.Clear();
    }

    // Moves the logo each frame and handles wall hits, corner hits, sounds, colours, and retargeting.
    private void BounceTimer_Tick(object? sender, EventArgs e)
    {
        if (!isBouncing)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var elapsedSeconds = Math.Min(0.04, (now - lastFrameTime).TotalSeconds);
        lastFrameTime = now;

        var maxX = GetMaxLogoX();
        var maxY = GetMaxLogoY();

        if (maxX <= 0 || maxY <= 0)
        {
            return;
        }

        var nextX = logoX + velocity.X * elapsedSeconds;
        var nextY = logoY + velocity.Y * elapsedSeconds;
        var hitX = false;
        var hitY = false;

        if (TryReachTargetCorner(nextX, nextY, out var targetCorner))
        {
            nextX = targetCorner.X;
            nextY = targetCorner.Y;
            velocity = new Vector(
                targetCorner.X <= 0 ? Math.Abs(velocity.X) : -Math.Abs(velocity.X),
                targetCorner.Y <= 0 ? Math.Abs(velocity.Y) : -Math.Abs(velocity.Y));
            hitX = true;
            hitY = true;
            currentCornerTarget = null;
        }
        else
        {
            if (nextX <= 0)
            {
                nextX = 0;
                velocity = new Vector(Math.Abs(velocity.X), velocity.Y);
                hitX = true;
            }
            else if (nextX >= maxX)
            {
                nextX = maxX;
                velocity = new Vector(-Math.Abs(velocity.X), velocity.Y);
                hitX = true;
            }

            if (nextY <= 0)
            {
                nextY = 0;
                velocity = new Vector(velocity.X, Math.Abs(velocity.Y));
                hitY = true;
            }
            else if (nextY >= maxY)
            {
                nextY = maxY;
                velocity = new Vector(velocity.X, -Math.Abs(velocity.Y));
                hitY = true;
            }
        }

        logoX = nextX;
        logoY = nextY;
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);

        var cornerHit = RecordCornerHit(maxX, maxY);

        if (cornerHit is not null)
        {
            NudgeLogoAwayFromCorner(cornerHit.Value, maxX, maxY);
        }

        if (hitX || hitY)
        {
            PlayRandomBounceSound();
            MaybeChangeLogoColor();
            currentCornerTarget = null;
            SetVelocityMagnitude(GetLogoSpeed());
            ApplySatisfactionBounce();
        }

        PushScreenToTvTexture();
    }

    // Plays one of the loaded bounce effects at random.
    private void PlayRandomBounceSound()
    {
        if (bounceClips.Count == 0)
        {
            return;
        }

        bounceClips[random.Next(bounceClips.Count)].PlayFromStart();
    }

    // Releases all loaded bounce sound players.
    private void CloseBounceClips()
    {
        foreach (var clip in bounceClips)
        {
            clip.Dispose();
        }

        bounceClips.Clear();
    }

    // Gives each bounce a 1-in-5 chance to change the logo colour.
    private void MaybeChangeLogoColor()
    {
        if (logoTemplate is null || random.Next(5) != 0)
        {
            return;
        }

        var nextColor = LogoColors[random.Next(LogoColors.Length)];

        if (LogoColors.Length > 1)
        {
            while (nextColor == currentLogoColor)
            {
                nextColor = LogoColors[random.Next(LogoColors.Length)];
            }
        }

        ApplyLogoColor(nextColor);
    }

    // Applies a colour to the logo while keeping the white background transparent.
    private void ApplyLogoColor(Color color)
    {
        if (logoTemplate is null)
        {
            return;
        }

        currentLogoColor = color;
        DvdLogoImage.Source = CreateTintedLogo(logoTemplate, color, out currentLogoPixels);
        PushScreenToTvTexture();
    }

    // Rebuilds the logo bitmap by tinting dark pixels and clearing pale background pixels.
    private WriteableBitmap CreateTintedLogo(Bitmap source, Color color, out byte[] tintedPixels)
    {
        var bitmap = new WriteableBitmap(source.PixelSize, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using var framebuffer = bitmap.Lock();
        source.CopyPixels(framebuffer);

        var bufferLength = framebuffer.RowBytes * framebuffer.Size.Height;
        var pixels = new byte[bufferLength];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);
        currentLogoPixelWidth = framebuffer.Size.Width;
        currentLogoPixelHeight = framebuffer.Size.Height;
        currentLogoPixelStride = framebuffer.RowBytes;

        for (var y = 0; y < framebuffer.Size.Height; y++)
        {
            var rowOffset = y * framebuffer.RowBytes;

            for (var x = 0; x < framebuffer.Size.Width; x++)
            {
                var index = rowOffset + (x * 4);
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                var alpha = pixels[index + 3] / 255.0;
                var brightness = (red + green + blue) / 3.0;
                var inkStrength = Math.Clamp((255.0 - brightness) / 255.0 * 1.4, 0.0, 1.0);
                var tintedAlpha = alpha * inkStrength;

                if (tintedAlpha < 0.04)
                {
                    pixels[index] = 0;
                    pixels[index + 1] = 0;
                    pixels[index + 2] = 0;
                    pixels[index + 3] = 0;
                    continue;
                }

                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = (byte)Math.Round(tintedAlpha * 255);
            }
        }

        Marshal.Copy(pixels, 0, framebuffer.Address, pixels.Length);
        tintedPixels = pixels;

        return bitmap;
    }

    // Updates the satisfaction label/slider art and breaks a locked perfect path when the value drops below 100%.
    private void SatisfactionSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (SatisfactionValueText is not null)
        {
            SatisfactionValueText.Text = $"{Math.Round(e.NewValue)}%";
        }

        UpdateSatisfactionSliderVisual();

        if (isBouncing)
        {
            SetVelocityMagnitude(GetLogoSpeed());

            if (e.NewValue < 100 && (e.OldValue >= 100 || currentCornerTarget is not null))
            {
                currentCornerTarget = null;
                ApplyTrajectoryMistake();
            }
        }

        WakeSatisfactionPanel();
    }

    // Keeps the custom slider fill and thumb aligned when the slider area resizes.
    private void SatisfactionSliderShell_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateSatisfactionSliderVisual();
    }

    // Starts dragging satisfaction from anywhere inside the larger invisible slider reach area.
    private void SatisfactionSliderShell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        isDraggingSatisfaction = true;
        e.Pointer.Capture(SatisfactionSliderShell);
        SetSatisfactionFromPointer(e);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Updates satisfaction while dragging through the larger invisible slider reach area.
    private void SatisfactionSliderShell_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isDraggingSatisfaction)
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(SatisfactionSliderShell);

        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            isDraggingSatisfaction = false;
            e.Pointer.Capture(null);
            return;
        }

        SetSatisfactionFromPointer(e);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Ends satisfaction dragging when the pointer is released.
    private void SatisfactionSliderShell_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isDraggingSatisfaction)
        {
            return;
        }

        SetSatisfactionFromPointer(e);
        isDraggingSatisfaction = false;
        e.Pointer.Capture(null);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Cancels satisfaction dragging if Avalonia gives pointer capture to something else.
    private void SatisfactionSliderShell_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isDraggingSatisfaction = false;
    }

    // Converts a pointer X position inside the slider shell into a satisfaction percentage.
    private void SetSatisfactionFromPointer(PointerEventArgs e)
    {
        SetSliderFromPointer(e, SatisfactionSliderShell, SatisfactionSlider);
    }

    // Draws the custom slim satisfaction slider based on the real slider value.
    private void UpdateSatisfactionSliderVisual()
    {
        if (SatisfactionSliderShell is null || SatisfactionSliderFill is null || SatisfactionSliderThumb is null)
        {
            return;
        }

        UpdateSliderVisual(SatisfactionSliderShell, SatisfactionSliderFill, SatisfactionSliderThumb, SatisfactionSlider);
    }

    // Updates the Fresnel value and redraws the glass overlay.
    private void GlassFresnelSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (GlassFresnelValueText is not null)
        {
            GlassFresnelValueText.Text = $"{Math.Round(e.NewValue)}%";
        }

        UpdateGlassFresnelSliderVisual();
        UpdateCrtGlassFresnel();
        WakeSatisfactionPanel();
    }

    // Controls how strongly the imported TV glass texture is drawn over the live signal.
    private void GlassTextureOpacitySlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (GlassTextureOpacityValueText is not null)
        {
            GlassTextureOpacityValueText.Text = $"{Math.Round(e.NewValue)}%";
        }

        TvModelStage?.SetScreenGlassOpacity(e.NewValue / 100);
        WakeSatisfactionPanel();
    }

    // Keeps the custom Fresnel slider aligned when its area resizes.
    private void GlassFresnelSliderShell_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateGlassFresnelSliderVisual();
    }

    // Starts dragging the Fresnel slider.
    private void GlassFresnelSliderShell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        isDraggingGlassFresnel = true;
        e.Pointer.Capture(GlassFresnelSliderShell);
        SetGlassFresnelFromPointer(e);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Updates the Fresnel value while dragging.
    private void GlassFresnelSliderShell_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isDraggingGlassFresnel)
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(GlassFresnelSliderShell);

        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            isDraggingGlassFresnel = false;
            e.Pointer.Capture(null);
            return;
        }

        SetGlassFresnelFromPointer(e);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Ends Fresnel dragging when the pointer is released.
    private void GlassFresnelSliderShell_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isDraggingGlassFresnel)
        {
            return;
        }

        SetGlassFresnelFromPointer(e);
        isDraggingGlassFresnel = false;
        e.Pointer.Capture(null);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Cancels Fresnel dragging if pointer capture moves.
    private void GlassFresnelSliderShell_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isDraggingGlassFresnel = false;
    }

    // Converts a pointer X position into a Fresnel percentage.
    private void SetGlassFresnelFromPointer(PointerEventArgs e)
    {
        SetSliderFromPointer(e, GlassFresnelSliderShell, GlassFresnelSlider);
    }

    // Draws the custom slim Fresnel slider based on the real slider value.
    private void UpdateGlassFresnelSliderVisual()
    {
        if (GlassFresnelSliderShell is null || GlassFresnelSliderFill is null || GlassFresnelSliderThumb is null)
        {
            return;
        }

        UpdateSliderVisual(GlassFresnelSliderShell, GlassFresnelSliderFill, GlassFresnelSliderThumb, GlassFresnelSlider);
    }

    // Updates CRT slider labels and redraws the separate CRT simulation.
    private void CrtEffectSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateCrtEffectValueTexts();
        UpdateCrtEffectSettings();
        WakeSatisfactionPanel();
    }

    // Gives the CRT effect sliders predictable track-click and drag behavior.
    private void CrtEffectSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        activeCrtEffectSlider = slider;
        e.Pointer.Capture(slider);
        SetSliderFromPointer(e, slider, slider);
        e.Handled = true;
    }

    // Keeps the selected CRT effect in sync while the pointer is dragged.
    private void CrtEffectSlider_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (activeCrtEffectSlider is null || !ReferenceEquals(sender, activeCrtEffectSlider))
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(activeCrtEffectSlider);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            activeCrtEffectSlider = null;
            e.Pointer.Capture(null);
            return;
        }

        SetSliderFromPointer(e, activeCrtEffectSlider, activeCrtEffectSlider);
        e.Handled = true;
    }

    // Releases the explicit CRT effect drag capture.
    private void CrtEffectSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (activeCrtEffectSlider is null || !ReferenceEquals(sender, activeCrtEffectSlider))
        {
            return;
        }

        SetSliderFromPointer(e, activeCrtEffectSlider, activeCrtEffectSlider);
        activeCrtEffectSlider = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // Sends all TV effect settings to the separate CRT overlay.
    private void UpdateCrtEffectSettings()
    {
        if (CrtCurveSlider is null
            || CrtScanlineSlider is null
            || CrtNoiseSlider is null
            || CrtGlitchSlider is null
            || CrtVignetteSlider is null
            || GlassFresnelSlider is null)
        {
            return;
        }

        TvModelStage?.SetCrtEffectSettings(
            GetPercentValue(CrtCurveSlider),
            GetPercentValue(CrtScanlineSlider),
            GetPercentValue(CrtNoiseSlider),
            GetPercentValue(CrtGlitchSlider),
            GetPercentValue(CrtVignetteSlider),
            GetPercentValue(GlassFresnelSlider));
        PushScreenToTvTexture();
    }

    // Keeps all CRT labels in sync with their sliders.
    private void UpdateCrtEffectValueTexts()
    {
        SetPercentText(CrtCurveValueText, CrtCurveSlider);
        SetPercentText(CrtScanlineValueText, CrtScanlineSlider);
        SetPercentText(CrtNoiseValueText, CrtNoiseSlider);
        SetPercentText(CrtGlitchValueText, CrtGlitchSlider);
        SetPercentText(CrtVignetteValueText, CrtVignetteSlider);
    }

    private static double GetPercentValue(Slider slider)
    {
        return Math.Clamp(slider.Value / 100.0, 0, 1);
    }

    private static void SetPercentText(TextBlock? text, Slider? slider)
    {
        if (text is null || slider is null)
        {
            return;
        }

        text.Text = $"{Math.Round(slider.Value)}%";
    }

    // Compatibility path for the custom Fresnel slider.
    private void UpdateCrtGlassFresnel()
    {
        UpdateCrtEffectSettings();
    }

    // Updates the corner value and redraws the screen frame.
    private void CornerRadiusSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (CornerRadiusValueText is not null)
        {
            CornerRadiusValueText.Text = $"{Math.Round(e.NewValue)}px";
        }

        UpdateCornerRadiusSliderVisual();
        UpdateScreenCornerRadius();
        WakeSatisfactionPanel();
    }

    // Keeps the custom corner slider aligned when its area resizes.
    private void CornerRadiusSliderShell_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateCornerRadiusSliderVisual();
    }

    // Starts dragging the corner radius slider.
    private void CornerRadiusSliderShell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        isDraggingCornerRadius = true;
        e.Pointer.Capture(CornerRadiusSliderShell);
        SetCornerRadiusFromPointer(e);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Updates the corner radius while dragging.
    private void CornerRadiusSliderShell_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isDraggingCornerRadius)
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(CornerRadiusSliderShell);

        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            isDraggingCornerRadius = false;
            e.Pointer.Capture(null);
            return;
        }

        SetCornerRadiusFromPointer(e);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Ends corner radius dragging when the pointer is released.
    private void CornerRadiusSliderShell_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isDraggingCornerRadius)
        {
            return;
        }

        SetCornerRadiusFromPointer(e);
        isDraggingCornerRadius = false;
        e.Pointer.Capture(null);
        WakeSatisfactionPanel();
        e.Handled = true;
    }

    // Cancels corner radius dragging if pointer capture moves.
    private void CornerRadiusSliderShell_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isDraggingCornerRadius = false;
    }

    // Converts a pointer X position into a corner radius value.
    private void SetCornerRadiusFromPointer(PointerEventArgs e)
    {
        SetSliderFromPointer(e, CornerRadiusSliderShell, CornerRadiusSlider);
    }

    // Draws the custom slim corner radius slider based on the real slider value.
    private void UpdateCornerRadiusSliderVisual()
    {
        if (CornerRadiusSliderShell is null || CornerRadiusSliderFill is null || CornerRadiusSliderThumb is null)
        {
            return;
        }

        UpdateSliderVisual(CornerRadiusSliderShell, CornerRadiusSliderFill, CornerRadiusSliderThumb, CornerRadiusSlider);
    }

    // Applies one radius to the light bezel, glass, and corner-depth pass.
    private void UpdateScreenCornerRadius()
    {
        if (ScreenBezel is null || ScreenClip is null || CornerDepthOverlay is null)
        {
            return;
        }

        var radius = Math.Clamp(CornerRadiusSlider.Value, CornerRadiusSlider.Minimum, CornerRadiusSlider.Maximum);
        ScreenBezel.CornerRadius = new CornerRadius(radius);
        ScreenClip.CornerRadius = new CornerRadius(Math.Max(0, radius - 2));
        CornerDepthOverlay.CornerRadius = radius;
    }

    // Converts a pointer X position inside a slider shell into that slider's value.
    private static void SetSliderFromPointer(PointerEventArgs e, Control sliderShell, Slider slider)
    {
        var width = sliderShell.Bounds.Width;

        if (width <= 0)
        {
            return;
        }

        var position = e.GetPosition(sliderShell);
        var progress = Math.Clamp(position.X / width, 0, 1);
        var range = slider.Maximum - slider.Minimum;

        slider.Value = slider.Minimum + (range * progress);
    }

    // Draws one of the custom slim sliders.
    private static void UpdateSliderVisual(Control sliderShell, Border sliderFill, Control sliderThumb, Slider slider)
    {
        var sliderRange = slider.Maximum - slider.Minimum;
        var progress = sliderRange <= 0
            ? 0
            : (slider.Value - slider.Minimum) / sliderRange;
        var width = sliderShell.Bounds.Width;

        if (width <= 0)
        {
            return;
        }

        progress = Math.Clamp(progress, 0, 1);
        sliderFill.Width = width * progress;

        var thumbWidth = sliderThumb.Width > 0 ? sliderThumb.Width : 18;
        var thumbTravel = Math.Max(0, width - thumbWidth);

        if (sliderThumb.RenderTransform is TranslateTransform thumbTransform)
        {
            thumbTransform.X = thumbTravel * progress;
        }
    }

    // Starts dissolving the satisfaction panel after it has not been touched for two seconds.
    private void SatisfactionFadeTimer_Tick(object? sender, EventArgs e)
    {
        satisfactionFadeTimer.Stop();

        if (KeepSatisfactionVisibleToggle.IsChecked == true || !isBouncing)
        {
            return;
        }

        AnimateSatisfactionPanelOpacity(0, TimeSpan.FromMilliseconds(650), hideWhenComplete: true);
    }

    // Keeps the satisfaction panel visible while the pointer is over it.
    private void SatisfactionPanel_PointerEntered(object? sender, PointerEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    // Keeps the satisfaction panel visible while the pointer moves across it.
    private void SatisfactionPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    // Keeps the satisfaction panel visible when the user presses inside it.
    private void SatisfactionPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    // Toggles whether the satisfaction panel should stay on screen permanently.
    private void KeepSatisfactionVisibleToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateKeepVisibleOption();
        WakeSatisfactionPanel();

        if (KeepSatisfactionVisibleToggle.IsChecked == true)
        {
            satisfactionFadeTimer.Stop();
        }
    }

    // Updates the small option box so the mark matches the toggle state.
    private void UpdateKeepVisibleOption()
    {
        var isChecked = KeepSatisfactionVisibleToggle.IsChecked == true;

        OptionMark.IsVisible = isChecked;
        OptionBox.Background = new SolidColorBrush(isChecked ? Color.Parse("#DDE4DF") : Color.Parse("#9CA5A2"));
        OptionBox.BorderBrush = new SolidColorBrush(isChecked ? Color.Parse("#4E5757") : Color.Parse("#4E5757"));
    }

    // Sends the debug camera values to the 3D TV stage.
    private void UpdateTvDebugCamera()
    {
        if (TvModelStage is null
            || CameraHeadingSlider is null
            || CameraAttitudeSlider is null
            || CameraDistanceSlider is null)
        {
            return;
        }

        TvModelStage.SetCamera(
            CameraHeadingSlider.Value,
            CameraAttitudeSlider.Value,
            CameraDistanceSlider.Value);
    }

    // Resets the debug camera controls to the default front view.
    private void ResetDebugCamera()
    {
        if (CameraHeadingSlider is null
            || CameraAttitudeSlider is null
            || CameraDistanceSlider is null)
        {
            return;
        }

        CameraHeadingSlider.Value = 0;
        CameraAttitudeSlider.Value = -2;
        CameraDistanceSlider.Value = 3.25;
        TvModelStage?.ResetCamera();
    }

    // Shows the option drawer and restarts its auto-hide timer when needed.
    private void WakeSatisfactionPanel()
    {
        ShowDebugControlsNow();
        satisfactionFadeTimer.Stop();
    }

    // Keeps the debug rail visible while the 3D integration is being tuned.
    private void ShowDebugControlsNow()
    {
        if (ControlsPanel is null)
        {
            return;
        }

        satisfactionFadeTimer.Stop();
        satisfactionOpacityCancellation?.Cancel();
        ControlsPanel.Opacity = 1;
        ControlsPanel.IsHitTestVisible = true;
        ControlsPanel.IsVisible = true;
    }

    // Shows the startup hint only while the TV is waiting for the first power press.
    private void SetPowerStartHintVisible(bool isVisible)
    {
        if (PowerStartHint is null)
        {
            return;
        }

        PowerStartHint.Opacity = isVisible ? 1 : 0;
        PowerStartHint.IsVisible = isVisible;
    }

    // Smoothly fades the option drawer and fully hides it after the fade-out finishes.
    private async void AnimateSatisfactionPanelOpacity(double opacity, TimeSpan duration, bool hideWhenComplete)
    {
        satisfactionOpacityCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        satisfactionOpacityCancellation = cancellation;

        var startOpacity = ControlsPanel.Opacity;
        var startTime = DateTime.UtcNow;

        try
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                var elapsed = DateTime.UtcNow - startTime;
                var progress = Clamp01(elapsed.TotalMilliseconds / duration.TotalMilliseconds);
                ControlsPanel.Opacity = Lerp(startOpacity, opacity, EaseOutQuad(progress));

                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(16, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ControlsPanel.Opacity = opacity;

        if (hideWhenComplete)
        {
            ControlsPanel.IsHitTestVisible = false;
            ControlsPanel.IsVisible = false;
        }
    }

    // Repositions or clamps the logo when the bounce field changes size.
    private void BounceStage_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!isBouncing)
        {
            PositionLogoForIntro();
            return;
        }

        ClampLogoPosition();
    }

    // Creates the first movement direction using the fixed logo speed.
    private Vector CreateInitialVelocity()
    {
        var angle = (random.NextDouble() * 0.55) + 0.45;
        var directionX = random.Next(0, 2) == 0 ? -1 : 1;
        var directionY = random.Next(0, 2) == 0 ? -1 : 1;
        var speed = GetLogoSpeed();

        return new Vector(Math.Cos(angle) * speed * directionX, Math.Sin(angle) * speed * directionY);
    }

    // Decides whether the next bounce should aim perfectly or drift with a mistake.
    private void ApplySatisfactionBounce()
    {
        var target = GetTargetCorner();

        if (ShouldUsePerfectBounce())
        {
            AimAtTargetCorner(target);
            return;
        }

        ApplyTrajectoryMistake();
    }

    // Converts the satisfaction slider percentage into a success/failure chance.
    private bool ShouldUsePerfectBounce()
    {
        var satisfaction = Math.Clamp(SatisfactionSlider.Value, 0, 100);

        if (satisfaction >= 100)
        {
            return true;
        }

        if (satisfaction <= 0)
        {
            return false;
        }

        return random.NextDouble() < satisfaction / 100.0;
    }

    // Points the logo directly at the chosen corner and remembers that target.
    private void AimAtTargetCorner(Point target)
    {
        var targetDirection = new Vector(target.X - logoX, target.Y - logoY);

        if (targetDirection.Length <= 0)
        {
            return;
        }

        velocity = targetDirection.Normalize() * GetLogoSpeed();
        currentCornerTarget = target;
    }

    // Rotates the current path by a small random error so an imperfect bounce misses the corner.
    private void ApplyTrajectoryMistake()
    {
        if (velocity.Length <= 0)
        {
            velocity = CreateInitialVelocity();
            currentCornerTarget = null;
            return;
        }

        var satisfaction = Math.Clamp(SatisfactionSlider.Value, 0, 99);
        var minimumMistakeDegrees = 4 + ((99 - satisfaction) * 0.08);
        var maximumMistakeDegrees = 14 + ((99 - satisfaction) * 0.18);
        var mistakeDegrees = minimumMistakeDegrees + random.NextDouble() * (maximumMistakeDegrees - minimumMistakeDegrees);

        if (random.Next(0, 2) == 0)
        {
            mistakeDegrees = -mistakeDegrees;
        }

        velocity = RotateByDegrees(velocity, mistakeDegrees);
        SetVelocityMagnitude(GetLogoSpeed());
        currentCornerTarget = null;
    }

    // Snaps the logo exactly to a perfect target corner when this frame reaches it.
    private bool TryReachTargetCorner(double nextX, double nextY, out Point targetCorner)
    {
        targetCorner = default;

        if (currentCornerTarget is not { } target)
        {
            return false;
        }

        var currentPosition = new Point(logoX, logoY);
        var nextPosition = new Point(nextX, nextY);
        var stepDistance = GetDistance(nextPosition, currentPosition);
        var distanceToTarget = GetDistance(target, currentPosition);

        if (stepDistance + 1.5 < distanceToTarget)
        {
            return false;
        }

        targetCorner = target;
        return true;
    }

    // Records a real corner hit and returns which corner was touched.
    private StageCorner? RecordCornerHit(double maxX, double maxY)
    {
        if (!IsLogoInCorner(maxX, maxY))
        {
            return null;
        }

        var corner = GetCurrentCorner(maxX, maxY);
        lastCornerHit = corner;
        RememberCornerHit(corner);

        return corner;
    }

    // Maintains a recent unique corner list so the app avoids repeating corners too soon.
    private void RememberCornerHit(StageCorner corner)
    {
        recentCornerHits.Remove(corner);
        recentCornerHits.Add(corner);

        var maximumHistory = Math.Min(CornerRepeatWindow - 1, GetStageCorners().Length);

        while (recentCornerHits.Count > maximumHistory)
        {
            recentCornerHits.RemoveAt(0);
        }
    }

    // Moves the logo slightly away from a corner so the same hit is not counted over and over.
    private void NudgeLogoAwayFromCorner(StageCorner corner, double maxX, double maxY)
    {
        const double inset = 2;

        logoX = IsLeft(corner) ? Math.Min(inset, maxX) : Math.Max(0, maxX - inset);
        logoY = IsTop(corner) ? Math.Min(inset, maxY) : Math.Max(0, maxY - inset);
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);
    }

    // Checks whether the logo is touching both a horizontal and vertical edge at the same time.
    private bool IsLogoInCorner(double maxX, double maxY)
    {
        const double tolerance = 0.5;
        var atHorizontalEdge = logoX <= tolerance || logoX >= maxX - tolerance;
        var atVerticalEdge = logoY <= tolerance || logoY >= maxY - tolerance;

        return atHorizontalEdge && atVerticalEdge;
    }

    // Keeps the logo inside the bounce field.
    private void ClampLogoPosition()
    {
        logoX = Math.Clamp(logoX, 0, GetMaxLogoX());
        logoY = Math.Clamp(logoY, 0, GetMaxLogoY());
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);
    }

    // Keeps the movement direction but changes its speed to the requested amount.
    private void SetVelocityMagnitude(double speed)
    {
        if (velocity.Length <= 0)
        {
            velocity = CreateInitialVelocity();
            return;
        }

        velocity = velocity.Normalize() * speed;
    }

    // Returns the fixed movement speed for the logo.
    private static double GetLogoSpeed()
    {
        return FixedLogoSpeed;
    }

    // Chooses the next corner target while avoiding the last/recent corners where possible.
    private Point GetTargetCorner()
    {
        var corners = GetStageCorners();
        var currentPosition = new Point(logoX, logoY);
        var maxX = GetMaxLogoX();
        var maxY = GetMaxLogoY();
        var currentCorner = IsLogoInCorner(maxX, maxY)
            ? GetCurrentCorner(maxX, maxY)
            : (StageCorner?)null;
        var viableCorners = corners.AsEnumerable();

        if (currentCorner is not null)
        {
            viableCorners = viableCorners.Where(corner => corner.Corner != currentCorner.Value);
        }

        var repeatSafeCorners = GetRepeatSafeCorners(viableCorners);

        if (lastCornerHit is not null)
        {
            if (repeatSafeCorners.Any(corner => !recentCornerHits.Contains(corner.Corner)))
            {
                var preferredCorners = repeatSafeCorners
                    .Where(corner => corner.Corner != lastCornerHit.Value)
                    .Where(corner => !SharesSide(corner.Corner, lastCornerHit.Value))
                    .ToArray();

                if (preferredCorners.Length > 0)
                {
                    return GetBestCornerPosition(preferredCorners, currentPosition);
                }
            }

            var differentCornerCandidates = repeatSafeCorners
                .Where(corner => corner.Corner != lastCornerHit.Value)
                .ToArray();

            if (differentCornerCandidates.Length > 0)
            {
                return GetBestCornerPosition(differentCornerCandidates, currentPosition);
            }
        }

        var candidates = repeatSafeCorners;

        if (candidates.Length == 0)
        {
            candidates = corners;
        }

        return GetBestCornerPosition(candidates, currentPosition);
    }

    // Filters target corners to fresh corners first, or the oldest used corners if all are recent.
    private (StageCorner Corner, Point Position)[] GetRepeatSafeCorners(
        IEnumerable<(StageCorner Corner, Point Position)> corners)
    {
        var candidates = corners.ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        var freshCorners = candidates
            .Where(corner => !recentCornerHits.Contains(corner.Corner))
            .ToArray();

        if (freshCorners.Length > 0)
        {
            return freshCorners;
        }

        return candidates
            .OrderBy(corner => GetRecentCornerIndex(corner.Corner))
            .ToArray();
    }

    // Returns the current canvas coordinates for all four possible target corners.
    private (StageCorner Corner, Point Position)[] GetStageCorners()
    {
        var maxX = GetMaxLogoX();
        var maxY = GetMaxLogoY();

        return
        [
            (StageCorner.TopLeft, new Point(0, 0)),
            (StageCorner.TopRight, new Point(maxX, 0)),
            (StageCorner.BottomLeft, new Point(0, maxY)),
            (StageCorner.BottomRight, new Point(maxX, maxY))
        ];
    }

    // Works out which named corner the logo is closest to right now.
    private StageCorner GetCurrentCorner(double maxX, double maxY)
    {
        var isLeft = logoX <= maxX / 2;
        var isTop = logoY <= maxY / 2;

        return (isTop, isLeft) switch
        {
            (true, true) => StageCorner.TopLeft,
            (true, false) => StageCorner.TopRight,
            (false, true) => StageCorner.BottomLeft,
            _ => StageCorner.BottomRight
        };
    }

    // Picks the best target by freshness first and distance second.
    private Point GetBestCornerPosition(
        IEnumerable<(StageCorner Corner, Point Position)> corners,
        Point currentPosition)
    {
        var candidates = corners.ToArray();
        var hasFreshCorner = candidates.Any(corner => !recentCornerHits.Contains(corner.Corner));

        return candidates
            .OrderBy(corner => hasFreshCorner && recentCornerHits.Contains(corner.Corner) ? 1 : 0)
            .ThenBy(corner => hasFreshCorner ? 0 : GetRecentCornerIndex(corner.Corner))
            .ThenBy(corner => GetDistanceSquared(corner.Position, currentPosition))
            .First()
            .Position;
    }

    // Finds how recently a corner was hit; lower numbers are older.
    private int GetRecentCornerIndex(StageCorner corner)
    {
        var index = recentCornerHits.IndexOf(corner);

        return index < 0 ? int.MinValue : index;
    }

    // Rotates a movement vector by a given number of degrees.
    private static Vector RotateByDegrees(Vector vector, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        return new Vector(
            (vector.X * cos) - (vector.Y * sin),
            (vector.X * sin) + (vector.Y * cos));
    }

    // Measures the straight-line distance between two points.
    private static double GetDistance(Point first, Point second)
    {
        return Math.Sqrt(GetDistanceSquared(first, second));
    }

    // Measures distance without a square root for cheaper sorting/comparison.
    private static double GetDistanceSquared(Point first, Point second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;

        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    // Checks whether two corners share the same top/bottom side or left/right side.
    private static bool SharesSide(StageCorner first, StageCorner second)
    {
        return IsTop(first) == IsTop(second) || IsLeft(first) == IsLeft(second);
    }

    // Returns whether a corner is on the top edge.
    private static bool IsTop(StageCorner corner)
    {
        return corner is StageCorner.TopLeft or StageCorner.TopRight;
    }

    // Returns whether a corner is on the left edge.
    private static bool IsLeft(StageCorner corner)
    {
        return corner is StageCorner.TopLeft or StageCorner.BottomLeft;
    }

    // Calculates the centered X position for the logo.
    private double GetCenteredLogoX()
    {
        return Math.Max(0, (BounceStage.Bounds.Width - DvdLogoImage.Width) / 2);
    }

    // Calculates the centered Y position for the logo.
    private double GetCenteredLogoY()
    {
        return Math.Max(0, (BounceStage.Bounds.Height - DvdLogoImage.Height) / 2);
    }

    // Calculates the lifted intro Y position above the exact center.
    private double GetIntroLogoY()
    {
        var centeredY = GetCenteredLogoY();
        var lift = Math.Min(135, BounceStage.Bounds.Height * 0.24);

        return Math.Max(24, centeredY - lift);
    }

    // Calculates the furthest right the logo can move without leaving the bounce field.
    private double GetMaxLogoX()
    {
        return Math.Max(0, BounceStage.Bounds.Width - DvdLogoImage.Width);
    }

    // Calculates the lowest the logo can move without leaving the bounce field.
    private double GetMaxLogoY()
    {
        return Math.Max(0, BounceStage.Bounds.Height - DvdLogoImage.Height);
    }

    // Falls back to a safe canvas value when Avalonia has not measured a position yet.
    private static double GetValidCanvasValue(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }

    // Builds the copied output path for an asset file.
    private static string GetOutputAssetPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
    }

    // Sends the current DVD screen state into the 3D TV screen material.
    private void PushScreenToTvTexture()
    {
        if (TvModelStage is null
            || BounceStage is null
            || DvdLogoImage is null
            || PowerOffOverlay is null
            || PowerFlickerOverlay is null)
        {
            return;
        }

        var stageWidth = BounceStage.Bounds.Width > 0 ? BounceStage.Bounds.Width : 640;
        var stageHeight = BounceStage.Bounds.Height > 0 ? BounceStage.Bounds.Height : 424;
        var currentLogoX = GetValidCanvasValue(Canvas.GetLeft(DvdLogoImage), logoX);
        var currentLogoY = GetValidCanvasValue(Canvas.GetTop(DvdLogoImage), logoY);

        TvModelStage.UpdateScreenTexture(
            currentLogoPixels,
            currentLogoPixelWidth,
            currentLogoPixelHeight,
            currentLogoPixelStride,
            currentLogoX,
            currentLogoY,
            DvdLogoImage.Width,
            DvdLogoImage.Height,
            stageWidth,
            stageHeight,
            DvdLogoImage.Opacity,
            PowerOffOverlay.Opacity,
            PowerFlickerOverlay.Opacity);
    }

    // Keeps a percentage-style number between 0 and 1.
    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    // Blends between two numbers by the given amount.
    private static double Lerp(double start, double end, double amount)
    {
        return start + ((end - start) * amount);
    }

    // Gives fade animations a softer ending.
    private static double EaseOutQuad(double amount)
    {
        return 1 - Math.Pow(1 - amount, 2);
    }

    // Gives movement animations a soft start and soft ending.
    private static double EaseInOutCubic(double amount)
    {
        return amount < 0.5
            ? 4 * amount * amount * amount
            : 1 - Math.Pow(-2 * amount + 2, 3) / 2;
    }
}

// Small NAudio wrapper that lets the app replay an MP3 sound from the beginning.
internal sealed class AudioClip : IDisposable
{
    private readonly AudioFileReader reader;
    private readonly WaveOut output;

    // Loads one MP3 file and sets its playback volume.
    public AudioClip(string path, float volume)
    {
        reader = new AudioFileReader(path)
        {
            Volume = volume
        };
        output = new WaveOut();
        output.Init(reader);
    }

    // Restarts the sound from the beginning and plays it.
    public void PlayFromStart()
    {
        output.Stop();
        reader.Position = 0;
        output.Play();
    }

    // Releases the file reader and audio output device.
    public void Dispose()
    {
        output.Dispose();
        reader.Dispose();
    }
}

// Draws raised and inset light on the screen corners.
public sealed class BezelCornerDepthControl : Control
{
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<BezelCornerDepthControl, double>(nameof(CornerRadius), 10.4);

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CornerRadiusProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = Math.Clamp(CornerRadius, 0, Math.Min(width, height) / 2);
        var outerLine = new Rect(2, 2, Math.Max(0, width - 4), Math.Max(0, height - 4));
        var innerShadow = new Rect(10, 10, Math.Max(0, width - 20), Math.Max(0, height - 20));
        var outerLightPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 188, 238, 255)), 2);
        var softLightPen = new Pen(new SolidColorBrush(Color.FromArgb(88, 237, 252, 255)), 1);
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 2);

        context.DrawRectangle(null, shadowPen, innerShadow, Math.Max(0, radius - 7), Math.Max(0, radius - 7));
        context.DrawRectangle(null, softLightPen, new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)), radius + 1, radius + 1);
        context.DrawRectangle(null, outerLightPen, outerLine, radius, radius);
    }
}

// Draws the small holes in the grey plastic speaker areas.
public sealed class SpeakerGrilleControl : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var panelBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
        var holeBrush = new SolidColorBrush(Color.FromArgb(96, 48, 56, 55));
        var ovalBrush = new SolidColorBrush(Color.FromArgb(36, 96, 106, 104));
        var ovalHoleBrush = new SolidColorBrush(Color.FromArgb(130, 34, 39, 38));
        var sheenPen = new Pen(new SolidColorBrush(Color.FromArgb(84, 255, 255, 255)), 1);
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(64, 72, 82, 80)), 1);
        var bounds = new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));

        context.DrawRectangle(panelBrush, shadowPen, bounds, 3, 3);
        context.DrawLine(sheenPen, bounds.TopLeft + new Vector(5, 3), bounds.TopRight - new Vector(5, -3));

        const double spacing = 4;
        const double radius = 0.85;

        for (var y = 7.0; y <= height - 7; y += spacing)
        {
            for (var x = 7.0; x <= width - 7; x += spacing)
            {
                context.DrawEllipse(holeBrush, null, new Point(x, y), radius, radius);
            }
        }

        var ovalWidth = Math.Min(width * 0.48, 46);
        var ovalHeight = Math.Min(height * 0.34, 116);
        var oval = new Rect(
            (width - ovalWidth) / 2,
            (height - ovalHeight) / 2,
            ovalWidth,
            ovalHeight);
        var ovalRadius = ovalWidth / 2;

        context.DrawRectangle(ovalBrush, shadowPen, oval, ovalRadius, ovalRadius);

        for (var y = oval.Top + 8; y <= oval.Bottom - 8; y += spacing)
        {
            for (var x = oval.Left + 8; x <= oval.Right - 8; x += spacing)
            {
                var normalizedX = (x - oval.Center.X) / (ovalWidth / 2);
                var normalizedY = (y - oval.Center.Y) / (ovalHeight / 2);

                if ((normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1)
                {
                    context.DrawEllipse(ovalHoleBrush, null, new Point(x, y), radius, radius);
                }
            }
        }
    }
}

// Draws the dark horizontal louver below the glass, matching the TV reference.
public sealed class ScreenLouverControl : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bodyBrush = new SolidColorBrush(Color.FromRgb(10, 12, 13));
        var lipBrush = new SolidColorBrush(Color.FromRgb(28, 31, 32));
        var groovePen = new Pen(new SolidColorBrush(Color.FromArgb(185, 45, 50, 52)), 1.2);
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), 1.4);
        var bounds = new Rect(0, 0, width, height);

        context.DrawRectangle(bodyBrush, null, bounds, 2, 2);
        context.DrawRectangle(lipBrush, null, new Rect(0, 0, width, Math.Min(8, height * 0.25)), 2, 2);

        for (var y = 11.5; y < height - 3; y += 4)
        {
            context.DrawLine(groovePen, new Point(0, y), new Point(width, y));
            context.DrawLine(shadowPen, new Point(0, y + 1.5), new Point(width, y + 1.5));
        }
    }
}
