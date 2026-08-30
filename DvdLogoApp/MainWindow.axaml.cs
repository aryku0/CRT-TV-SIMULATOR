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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NAudio.Wave;

namespace DvdLogoApp;

public partial class MainWindow : Window
{
    private const string LogoFileName = "DVD_video_logo.png";
    private const string IntroSoundFileName = "edr-old-pc-monitor-switch-on-and-degaussing-8576.mp3";
    private const double FixedLogoSpeed = 262;
    private const int CornerRepeatWindow = 5;

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
    private readonly DispatcherTimer introTimer;
    private readonly Random random = new();
    private readonly List<AudioClip> bounceClips = new();
    private readonly List<StageCorner> recentCornerHits = [];

    private CancellationTokenSource? satisfactionOpacityCancellation;
    private AudioClip? introClip;
    private Bitmap? logoTemplate;
    private Vector velocity = new(280, 190);
    private DateTime lastFrameTime;
    private DateTime introStartTime;
    private Point? currentCornerTarget;
    private StageCorner? lastCornerHit;
    private Color currentLogoColor = Colors.White;
    private double introStartY;
    private double introFinalY;
    private double logoX;
    private double logoY;
    private bool isDraggingSatisfaction;
    private bool isDraggingGlassFresnel;
    private bool isDraggingCornerRadius;
    private bool isBouncing;

    // Sets up the window, timers, and starting visual state.
    public MainWindow()
    {
        InitializeComponent();

        ScreenSurface.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        ScreenSurface.RenderTransform = new ScaleTransform(1.018, 1.012);
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

        satisfactionFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        satisfactionFadeTimer.Tick += SatisfactionFadeTimer_Tick;

        introTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        introTimer.Tick += IntroTimer_Tick;

        bounceTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        bounceTimer.Tick += BounceTimer_Tick;

        Loaded += Window_Loaded;
        KeyDown += Window_KeyDown;
        Closed += Window_Closed;
    }

    // Runs when the window opens: loads assets, plays the intro sound, and starts the intro animation.
    private void Window_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LoadLogo();
        LoadBounceSounds();
        PositionLogoForIntro();
        PlayIntroSound();
        BeginIntroAnimation();
        UpdateFullscreenButtonText();
    }

    // Stops timers and releases audio/image resources when the window closes.
    private void Window_Closed(object? sender, EventArgs e)
    {
        introTimer.Stop();
        bounceTimer.Stop();
        satisfactionFadeTimer.Stop();
        satisfactionOpacityCancellation?.Cancel();
        introClip?.Dispose();
        CloseBounceClips();
        logoTemplate?.Dispose();
    }

    // Loads the DVD logo from the bundled Avalonia app assets.
    private void LoadLogo()
    {
        var logoUri = new Uri($"avares://DvdLogoApp/Assets/{LogoFileName}");

        using var stream = AssetLoader.Open(logoUri);
        logoTemplate = new Bitmap(stream);
        ApplyLogoColor(Colors.White);
    }

    // Plays the CRT switch-on sound at startup if the MP3 is present.
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

    // Starts the logo fade-in and upward movement before the controls appear.
    private void BeginIntroAnimation()
    {
        introTimer.Stop();

        introStartY = GetCenteredLogoY();
        introFinalY = GetIntroLogoY();

        Canvas.SetLeft(DvdLogoImage, GetCenteredLogoX());
        Canvas.SetTop(DvdLogoImage, introStartY);
        DvdLogoImage.Opacity = 0;
        ControlsPanel.IsVisible = true;
        ControlsPanel.IsHitTestVisible = false;
        ControlsPanel.Opacity = 0;

        introStartTime = DateTime.UtcNow;
        introTimer.Start();
    }

    // Advances the intro animation a frame at a time.
    private void IntroTimer_Tick(object? sender, EventArgs e)
    {
        var elapsedSeconds = (DateTime.UtcNow - introStartTime).TotalSeconds;

        DvdLogoImage.Opacity = EaseOutQuad(Clamp01(elapsedSeconds / 2.4));

        if (elapsedSeconds >= 1.25)
        {
            var liftProgress = EaseInOutCubic(Clamp01((elapsedSeconds - 1.25) / 2.7));
            var currentY = Lerp(introStartY, introFinalY, liftProgress);
            Canvas.SetTop(DvdLogoImage, currentY);
            logoY = currentY;
        }

        if (elapsedSeconds >= 3.4)
        {
            ControlsPanel.Opacity = EaseOutQuad(Clamp01((elapsedSeconds - 3.4) / 0.75));
        }

        if (elapsedSeconds < 4.2)
        {
            return;
        }

        introTimer.Stop();
        DvdLogoImage.Opacity = 1;
        ControlsPanel.IsHitTestVisible = true;
        ControlsPanel.Opacity = 1;
        logoX = Canvas.GetLeft(DvdLogoImage);
        logoY = introFinalY;
        Canvas.SetTop(DvdLogoImage, logoY);
        WakeSatisfactionPanel();
    }

    // Places the logo in its pre-start intro position.
    private void PositionLogoForIntro()
    {
        logoX = GetCenteredLogoX();
        logoY = GetIntroLogoY();
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);
    }

    // Starts the bouncing mode when the Start button is clicked.
    private void StartButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StartBouncing();
    }

    // Switches between the normal window and fullscreen when the fullscreen button is clicked.
    private void FullscreenButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleFullscreen();
        WakeSatisfactionPanel();
    }

    // Opens or closes the right-side option drawer.
    private void OptionsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ControlsPanel.IsVisible && ControlsPanel.Opacity > 0.5)
        {
            satisfactionFadeTimer.Stop();
            AnimateSatisfactionPanelOpacity(0, TimeSpan.FromMilliseconds(180), hideWhenComplete: true);
            return;
        }

        WakeSatisfactionPanel();
    }

    // Lets Escape leave fullscreen without closing the app.
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || WindowState != WindowState.FullScreen)
        {
            return;
        }

        ExitFullscreen();
        e.Handled = true;
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

    // Switches from the intro screen into the live bouncing logo simulation.
    private void StartBouncing()
    {
        introTimer.Stop();

        DvdLogoImage.Opacity = 1;
        ControlsPanel.Opacity = 1;
        WakeSatisfactionPanel();

        logoX = GetValidCanvasValue(Canvas.GetLeft(DvdLogoImage), GetCenteredLogoX());
        logoY = GetValidCanvasValue(Canvas.GetTop(DvdLogoImage), GetIntroLogoY());
        ClampLogoPosition();

        velocity = CreateInitialVelocity();
        currentCornerTarget = null;
        lastCornerHit = null;
        recentCornerHits.Clear();
        ApplySatisfactionBounce();

        isBouncing = true;
        StartButton.Content = "Running";
        StartButton.IsEnabled = false;
        lastFrameTime = DateTime.UtcNow;
        bounceTimer.Start();
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
        DvdLogoImage.Source = CreateTintedLogo(logoTemplate, color);
    }

    // Rebuilds the logo bitmap by tinting dark pixels and clearing pale background pixels.
    private static WriteableBitmap CreateTintedLogo(Bitmap source, Color color)
    {
        var bitmap = new WriteableBitmap(source.PixelSize, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using var framebuffer = bitmap.Lock();
        source.CopyPixels(framebuffer);

        var bufferLength = framebuffer.RowBytes * framebuffer.Size.Height;
        var pixels = new byte[bufferLength];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);

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

    // Sends all TV effect settings to the separate CRT overlay.
    private void UpdateCrtEffectSettings()
    {
        if (CrtOverlay is null
            || CrtCurveSlider is null
            || CrtScanlineSlider is null
            || CrtNoiseSlider is null
            || CrtGlitchSlider is null
            || CrtVignetteSlider is null
            || GlassFresnelSlider is null)
        {
            return;
        }

        CrtOverlay.CurveStrength = GetPercentValue(CrtCurveSlider);
        CrtOverlay.ScanlineStrength = GetPercentValue(CrtScanlineSlider);
        CrtOverlay.NoiseStrength = GetPercentValue(CrtNoiseSlider);
        CrtOverlay.GlitchStrength = GetPercentValue(CrtGlitchSlider);
        CrtOverlay.VignetteStrength = GetPercentValue(CrtVignetteSlider);
        CrtOverlay.FresnelStrength = GetPercentValue(GlassFresnelSlider);
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
        OptionBox.Background = new SolidColorBrush(isChecked ? Color.Parse("#F7FAFC") : Color.Parse("#263140"));
        OptionBox.BorderBrush = new SolidColorBrush(isChecked ? Color.Parse("#F7FAFC") : Color.Parse("#4B5A6D"));
    }

    // Shows the option drawer and restarts its auto-hide timer when needed.
    private void WakeSatisfactionPanel()
    {
        if (ControlsPanel is null)
        {
            return;
        }

        ControlsPanel.IsVisible = true;
        ControlsPanel.IsHitTestVisible = true;
        AnimateSatisfactionPanelOpacity(1, TimeSpan.FromMilliseconds(180), hideWhenComplete: false);

        if (KeepSatisfactionVisibleToggle?.IsChecked == true)
        {
            satisfactionFadeTimer.Stop();
            return;
        }

        satisfactionFadeTimer.Stop();
        satisfactionFadeTimer.Start();
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

        var panelBrush = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));
        var holeBrush = new SolidColorBrush(Color.FromArgb(180, 9, 12, 14));
        var sheenPen = new Pen(new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)), 1);
        var bounds = new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));

        context.DrawRectangle(panelBrush, null, bounds, 5, 5);
        context.DrawLine(sheenPen, bounds.TopLeft + new Vector(8, 4), bounds.TopRight - new Vector(8, -4));

        const double spacing = 8;
        const double radius = 2;

        for (var y = 9.0; y <= height - 9; y += spacing)
        {
            var offset = ((int)(y / spacing) % 2) * 3.5;

            for (var x = 9.0 + offset; x <= width - 9; x += spacing)
            {
                context.DrawEllipse(holeBrush, null, new Point(x, y), radius, radius);
            }
        }
    }
}
