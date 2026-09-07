using System;
using System.IO;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Ab4d.SharpEngine.AvaloniaUI;
using Ab4d.SharpEngine.Cameras;
using Ab4d.SharpEngine.Common;
using Ab4d.SharpEngine.glTF;
using Ab4d.SharpEngine.Lights;
using Ab4d.SharpEngine.Materials;

namespace DvdLogoApp;

public sealed class TvModelPreviewWindow : Window
{
    private readonly SharpEngineSceneView sceneView;
    private readonly TextBlock statusText;
    private readonly DispatcherTimer initializationTimer;
    private bool hasLoadedModel;

    public TvModelPreviewWindow()
    {
        Title = "3D TV Preview";
        Width = 980;
        Height = 640;
        MinWidth = 760;
        MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(7, 10, 15));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        sceneView = new SharpEngineSceneView
        {
            StopRenderingWhenHidden = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        statusText = new TextBlock
        {
            Text = "Loading 3D TV...",
            Margin = new Avalonia.Thickness(18),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = Brushes.White,
            FontSize = 13,
            Opacity = 0.78,
            IsHitTestVisible = false
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(7, 10, 15))
        };

        root.Children.Add(sceneView);
        root.Children.Add(statusText);
        Content = root;

        initializationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        initializationTimer.Tick += InitializationTimer_Tick;

        Loaded += TvModelPreviewWindow_Loaded;
        Closed += TvModelPreviewWindow_Closed;
    }

    private void TvModelPreviewWindow_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        initializationTimer.Start();
    }

    private void TvModelPreviewWindow_Closed(object? sender, EventArgs e)
    {
        initializationTimer.Stop();
        sceneView.Dispose();
    }

    private void InitializationTimer_Tick(object? sender, EventArgs e)
    {
        if (hasLoadedModel || sceneView.GpuDevice is null || sceneView.Scene is null || sceneView.SceneView is null)
        {
            return;
        }

        initializationTimer.Stop();
        LoadModel();
    }

    private void LoadModel()
    {
        hasLoadedModel = true;

        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "retro_90s_tv");
        var modelPath = Path.Combine(modelDirectory, "scene.gltf");

        if (!File.Exists(modelPath))
        {
            statusText.Text = "3D model was not found in the app output.";
            return;
        }

        try
        {
            var scene = sceneView.Scene;
            var sceneViewState = sceneView.SceneView;

            scene.RootNode.Clear();
            scene.SetAmbientLight(0.72f);
            scene.Lights.Add(new DirectionalLight(new Vector3(-0.25f, -0.65f, -0.45f))
            {
                Color = new Color3(0.92f, 0.94f, 1f)
            });
            scene.Lights.Add(new DirectionalLight(new Vector3(0.55f, -0.35f, 0.32f))
            {
                Color = new Color3(0.48f, 0.5f, 0.54f)
            });

            var importer = new glTFImporter(new SkiaSharpBitmapIO(), sceneView.GpuDevice)
            {
                UsePbrMaterial = true
            };
            var tvModel = importer.Import(modelPath, modelDirectory, null);
            if (tvModel is null)
            {
                statusText.Text = "3D model importer did not return a scene.";
                return;
            }

            scene.RootNode.Add(tvModel);

            sceneViewState.BackgroundColor = new Color4(0.027f, 0.039f, 0.059f, 1f);
            sceneViewState.Camera = new TargetPositionCamera("TV preview camera")
            {
                TargetPosition = new Vector3(0, 0.15f, 0),
                Distance = 3.25f,
                Heading = 0,
                Attitude = -2,
                FieldOfView = 32
            };

            scene.Update(true);
            sceneView.RenderScene(forceUpdate: true, forceRender: true);
            statusText.Text = "3D TV preview";
        }
        catch (Exception exception)
        {
            statusText.Text = $"3D preview could not start: {exception.Message}";
        }
    }
}
