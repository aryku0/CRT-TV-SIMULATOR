using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DvdLogoApp;

public partial class MainWindow : Window
{
    private const string LogoFileName = "DVD_video_logo.png";
    private const string IntroSoundFileName = "edr-old-pc-monitor-switch-on-and-degaussing-8576.mp3";
    private const double FixedLogoSpeed = 262;

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
    private readonly MediaPlayer introPlayer = new();
    private readonly List<MediaPlayer> bouncePlayers = new();
    private readonly Random random = new();

    private Vector velocity = new(280, 190);
    private DateTime lastFrameTime;
    private DateTime lastCornerHitTime = DateTime.MinValue;
    private Point? currentCornerTarget;
    private StageCorner? lastCornerHit;
    private BitmapSource? logoTemplate;
    private Color currentLogoColor = Colors.White;
    private double logoX;
    private double logoY;
    private int cornerHits;
    private bool isBouncing;

    public MainWindow()
    {
        satisfactionFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        satisfactionFadeTimer.Tick += SatisfactionFadeTimer_Tick;

        InitializeComponent();
        LoadBundledFont();

        bounceTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        bounceTimer.Tick += BounceTimer_Tick;

        Loaded += Window_Loaded;
        Closed += (_, _) =>
        {
            bounceTimer.Stop();
            satisfactionFadeTimer.Stop();
            introPlayer.Close();
            CloseBouncePlayers();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadLogo();
        LoadBounceSounds();
        PositionLogoForIntro();
        PlayIntroSound();
        BeginIntroAnimation();
    }

    private void LoadLogo()
    {
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", LogoFileName);

        if (!File.Exists(logoPath))
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        logoTemplate = bitmap;
        ApplyLogoColor(Colors.White);
    }

    private void LoadBundledFont()
    {
        var fontDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

        if (!Directory.Exists(fontDirectory))
        {
            return;
        }

        var fontDirectoryUri = new Uri(fontDirectory + Path.DirectorySeparatorChar, UriKind.Absolute);
        FontFamily = new FontFamily(fontDirectoryUri, "./#Jost*");
    }

    private void PlayIntroSound()
    {
        var audioPath = Path.Combine(AppContext.BaseDirectory, "Assets", IntroSoundFileName);

        if (!File.Exists(audioPath))
        {
            return;
        }

        introPlayer.Open(new Uri(audioPath, UriKind.Absolute));
        introPlayer.Volume = 0.9;
        introPlayer.Play();
    }

    private void LoadBounceSounds()
    {
        CloseBouncePlayers();

        foreach (var fileName in BounceSoundFileNames)
        {
            var audioPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);

            if (!File.Exists(audioPath))
            {
                continue;
            }

            var player = new MediaPlayer
            {
                Volume = 0.62
            };
            player.Open(new Uri(audioPath, UriKind.Absolute));
            bouncePlayers.Add(player);
        }
    }

    private void BeginIntroAnimation()
    {
        var startY = GetCenteredLogoY();
        var finalY = GetIntroLogoY();

        Canvas.SetLeft(DvdLogoImage, GetCenteredLogoX());
        Canvas.SetTop(DvdLogoImage, startY);
        DvdLogoImage.Opacity = 0;
        ControlsPanel.Opacity = 0;
        CornerCounterPanel.Opacity = 0;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.4))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fadeIn.Completed += (_, _) => DvdLogoImage.Opacity = 1;

        var liftLogo = new DoubleAnimation(startY, finalY, TimeSpan.FromSeconds(2.7))
        {
            BeginTime = TimeSpan.FromSeconds(1.25),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        liftLogo.Completed += (_, _) =>
        {
            Canvas.SetTop(DvdLogoImage, finalY);
            logoX = Canvas.GetLeft(DvdLogoImage);
            logoY = finalY;
        };

        var revealControls = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.75))
        {
            BeginTime = TimeSpan.FromSeconds(3.4),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        revealControls.Completed += (_, _) =>
        {
            ControlsPanel.Opacity = 1;
            CornerCounterPanel.Opacity = 1;
            WakeSatisfactionPanel();
        };

        DvdLogoImage.BeginAnimation(OpacityProperty, fadeIn);
        DvdLogoImage.BeginAnimation(Canvas.TopProperty, liftLogo);
        ControlsPanel.BeginAnimation(OpacityProperty, revealControls);
        CornerCounterPanel.BeginAnimation(OpacityProperty, revealControls.Clone());
    }

    private void PositionLogoForIntro()
    {
        logoX = GetCenteredLogoX();
        logoY = GetIntroLogoY();
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartBouncing();
    }

    private void StartBouncing()
    {
        DvdLogoImage.BeginAnimation(OpacityProperty, null);
        DvdLogoImage.BeginAnimation(Canvas.TopProperty, null);
        ControlsPanel.BeginAnimation(OpacityProperty, null);
        CornerCounterPanel.BeginAnimation(OpacityProperty, null);

        DvdLogoImage.Opacity = 1;
        ControlsPanel.Opacity = 1;
        CornerCounterPanel.Opacity = 1;
        WakeSatisfactionPanel();

        logoX = GetValidCanvasValue(Canvas.GetLeft(DvdLogoImage), GetCenteredLogoX());
        logoY = GetValidCanvasValue(Canvas.GetTop(DvdLogoImage), GetIntroLogoY());
        ClampLogoPosition();

        cornerHits = 0;
        UpdateCornerHitText();

        velocity = CreateInitialVelocity();
        currentCornerTarget = null;
        lastCornerHit = null;
        SteerTowardTargetCorner();

        isBouncing = true;
        StartButton.Visibility = Visibility.Collapsed;
        lastFrameTime = DateTime.UtcNow;
        bounceTimer.Start();
    }

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
                velocity.X = Math.Abs(velocity.X);
                hitX = true;
            }
            else if (nextX >= maxX)
            {
                nextX = maxX;
                velocity.X = -Math.Abs(velocity.X);
                hitX = true;
            }

            if (nextY <= 0)
            {
                nextY = 0;
                velocity.Y = Math.Abs(velocity.Y);
                hitY = true;
            }
            else if (nextY >= maxY)
            {
                nextY = maxY;
                velocity.Y = -Math.Abs(velocity.Y);
                hitY = true;
            }
        }

        logoX = nextX;
        logoY = nextY;
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);

        if (IsLogoInCorner(maxX, maxY))
        {
            RegisterCornerHit(maxX, maxY);
        }

        if (hitX || hitY)
        {
            PlayRandomBounceSound();
            MaybeChangeLogoColor();
            currentCornerTarget = null;
            SetVelocityMagnitude(GetLogoSpeed());
            SteerTowardTargetCorner();
        }
    }

    private void PlayRandomBounceSound()
    {
        if (bouncePlayers.Count == 0)
        {
            return;
        }

        var player = bouncePlayers[random.Next(bouncePlayers.Count)];

        try
        {
            player.Stop();
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CloseBouncePlayers()
    {
        foreach (var player in bouncePlayers)
        {
            player.Close();
        }

        bouncePlayers.Clear();
    }

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

    private void ApplyLogoColor(Color color)
    {
        if (logoTemplate is null)
        {
            return;
        }

        currentLogoColor = color;
        DvdLogoImage.Source = CreateTintedLogo(logoTemplate, color);
    }

    private static BitmapSource CreateTintedLogo(BitmapSource source, Color color)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        converted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var blue = pixels[i];
            var green = pixels[i + 1];
            var red = pixels[i + 2];
            var alpha = pixels[i + 3] / 255.0;
            var brightness = (red + green + blue) / 3.0;
            var inkStrength = Math.Clamp((255.0 - brightness) / 255.0 * 1.4, 0.0, 1.0);
            var tintedAlpha = alpha * inkStrength;

            if (tintedAlpha < 0.04)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = 0;
                continue;
            }

            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = (byte)Math.Round(tintedAlpha * 255);
        }

        var tintedLogo = BitmapSource.Create(
            width,
            height,
            converted.DpiX,
            converted.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        tintedLogo.Freeze();

        return tintedLogo;
    }

    private void SatisfactionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SatisfactionValueText is not null)
        {
            SatisfactionValueText.Text = $"{Math.Round(e.NewValue)}%";
        }

        if (isBouncing)
        {
            SetVelocityMagnitude(GetLogoSpeed());
        }

        WakeSatisfactionPanel();
    }

    private void SatisfactionFadeTimer_Tick(object? sender, EventArgs e)
    {
        satisfactionFadeTimer.Stop();

        if (KeepSatisfactionVisibleCheckBox.IsChecked == true)
        {
            return;
        }

        AnimateSatisfactionPanelOpacity(0, TimeSpan.FromMilliseconds(650), hideWhenComplete: true);
    }

    private void SatisfactionPanel_MouseEnter(object sender, MouseEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    private void SatisfactionPanel_MouseMove(object sender, MouseEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    private void SatisfactionPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    private void SatisfactionRevealZone_MouseEnter(object sender, MouseEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    private void SatisfactionRevealZone_MouseMove(object sender, MouseEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    private void KeepSatisfactionVisibleCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        WakeSatisfactionPanel();
        satisfactionFadeTimer.Stop();
    }

    private void KeepSatisfactionVisibleCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        WakeSatisfactionPanel();
    }

    private void WakeSatisfactionPanel()
    {
        if (SatisfactionPanel is null)
        {
            return;
        }

        SatisfactionPanel.Visibility = Visibility.Visible;
        SatisfactionPanel.IsHitTestVisible = true;
        AnimateSatisfactionPanelOpacity(1, TimeSpan.FromMilliseconds(180), hideWhenComplete: false);

        if (KeepSatisfactionVisibleCheckBox?.IsChecked == true)
        {
            satisfactionFadeTimer.Stop();
            return;
        }

        satisfactionFadeTimer.Stop();
        satisfactionFadeTimer.Start();
    }

    private void AnimateSatisfactionPanelOpacity(double opacity, TimeSpan duration, bool hideWhenComplete)
    {
        SatisfactionPanel.BeginAnimation(OpacityProperty, null);

        var animation = new DoubleAnimation(SatisfactionPanel.Opacity, opacity, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            SatisfactionPanel.Opacity = opacity;

            if (hideWhenComplete)
            {
                SatisfactionPanel.IsHitTestVisible = false;
                SatisfactionPanel.Visibility = Visibility.Hidden;
            }
        };

        SatisfactionPanel.BeginAnimation(OpacityProperty, animation);
    }

    private void BounceStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!isBouncing)
        {
            PositionLogoForIntro();
            return;
        }

        ClampLogoPosition();
    }

    private Vector CreateInitialVelocity()
    {
        var angle = (random.NextDouble() * 0.55) + 0.45;
        var directionX = random.Next(0, 2) == 0 ? -1 : 1;
        var directionY = random.Next(0, 2) == 0 ? -1 : 1;
        var speed = GetLogoSpeed();

        return new Vector(Math.Cos(angle) * speed * directionX, Math.Sin(angle) * speed * directionY);
    }

    private void SteerTowardTargetCorner()
    {
        var steeringStrength = Math.Clamp(SatisfactionSlider.Value / 100.0, 0, 1);

        if (steeringStrength <= 0)
        {
            return;
        }

        var target = GetTargetCorner();
        var targetDirection = target - new Point(logoX, logoY);

        if (targetDirection.Length <= 0 || velocity.Length <= 0)
        {
            return;
        }

        targetDirection.Normalize();
        velocity.Normalize();
        velocity = RotateToward(velocity, targetDirection, steeringStrength) * GetLogoSpeed();
        currentCornerTarget = steeringStrength >= 0.99 ? target : null;
    }

    private bool TryReachTargetCorner(double nextX, double nextY, out Point targetCorner)
    {
        targetCorner = default;

        if (currentCornerTarget is not { } target)
        {
            return false;
        }

        var currentPosition = new Point(logoX, logoY);
        var nextPosition = new Point(nextX, nextY);
        var stepDistance = (nextPosition - currentPosition).Length;
        var distanceToTarget = (target - currentPosition).Length;

        if (stepDistance + 1.5 < distanceToTarget)
        {
            return false;
        }

        targetCorner = target;
        return true;
    }

    private void RegisterCornerHit(double maxX, double maxY)
    {
        var now = DateTime.UtcNow;

        if (now - lastCornerHitTime < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        cornerHits++;
        lastCornerHitTime = now;
        lastCornerHit = GetCurrentCorner(maxX, maxY);
        UpdateCornerHitText();
    }

    private void UpdateCornerHitText()
    {
        CornerHitText.Text = cornerHits.ToString();
    }

    private bool IsLogoInCorner(double maxX, double maxY)
    {
        const double tolerance = 0.5;
        var atHorizontalEdge = logoX <= tolerance || logoX >= maxX - tolerance;
        var atVerticalEdge = logoY <= tolerance || logoY >= maxY - tolerance;

        return atHorizontalEdge && atVerticalEdge;
    }

    private void ClampLogoPosition()
    {
        logoX = Math.Clamp(logoX, 0, GetMaxLogoX());
        logoY = Math.Clamp(logoY, 0, GetMaxLogoY());
        Canvas.SetLeft(DvdLogoImage, logoX);
        Canvas.SetTop(DvdLogoImage, logoY);
    }

    private void SetVelocityMagnitude(double speed)
    {
        if (velocity.Length <= 0)
        {
            velocity = CreateInitialVelocity();
            return;
        }

        velocity.Normalize();
        velocity *= speed;
    }

    private double GetLogoSpeed()
    {
        return FixedLogoSpeed;
    }

    private Point GetTargetCorner()
    {
        var corners = GetStageCorners();
        var currentPosition = new Point(logoX, logoY);
        var viableCorners = lastCornerHit is null
            ? corners
            : corners
                .Where(corner => corner.Corner != lastCornerHit.Value)
                .Where(corner => !SharesSide(corner.Corner, lastCornerHit.Value))
                .ToArray();

        if (viableCorners.Length == 0 && lastCornerHit is not null)
        {
            viableCorners = corners
                .Where(corner => corner.Corner != lastCornerHit.Value)
                .ToArray();
        }

        if (viableCorners.Length == 0)
        {
            viableCorners = corners;
        }

        return viableCorners
            .OrderBy(corner => (corner.Position - currentPosition).LengthSquared)
            .First()
            .Position;
    }

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

    private static Vector RotateToward(Vector currentDirection, Vector targetDirection, double strength)
    {
        var currentAngle = Math.Atan2(currentDirection.Y, currentDirection.X);
        var targetAngle = Math.Atan2(targetDirection.Y, targetDirection.X);
        var angleDifference = NormalizeRadians(targetAngle - currentAngle);
        var steeredAngle = currentAngle + (angleDifference * strength);

        return new Vector(Math.Cos(steeredAngle), Math.Sin(steeredAngle));
    }

    private static double NormalizeRadians(double radians)
    {
        while (radians > Math.PI)
        {
            radians -= Math.PI * 2;
        }

        while (radians < -Math.PI)
        {
            radians += Math.PI * 2;
        }

        return radians;
    }

    private static bool SharesSide(StageCorner first, StageCorner second)
    {
        return IsTop(first) == IsTop(second) || IsLeft(first) == IsLeft(second);
    }

    private static bool IsTop(StageCorner corner)
    {
        return corner is StageCorner.TopLeft or StageCorner.TopRight;
    }

    private static bool IsLeft(StageCorner corner)
    {
        return corner is StageCorner.TopLeft or StageCorner.BottomLeft;
    }

    private double GetCenteredLogoX()
    {
        return Math.Max(0, (BounceStage.ActualWidth - DvdLogoImage.Width) / 2);
    }

    private double GetCenteredLogoY()
    {
        return Math.Max(0, (BounceStage.ActualHeight - DvdLogoImage.Height) / 2);
    }

    private double GetIntroLogoY()
    {
        var centeredY = GetCenteredLogoY();
        var lift = Math.Min(135, BounceStage.ActualHeight * 0.24);

        return Math.Max(24, centeredY - lift);
    }

    private double GetMaxLogoX()
    {
        return Math.Max(0, BounceStage.ActualWidth - DvdLogoImage.Width);
    }

    private double GetMaxLogoY()
    {
        return Math.Max(0, BounceStage.ActualHeight - DvdLogoImage.Height);
    }

    private static double GetValidCanvasValue(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }
}
