using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Ab4d.SharpEngine.AvaloniaUI;
using Ab4d.SharpEngine.Cameras;
using Ab4d.SharpEngine.Common;
using Ab4d.SharpEngine.Core;
using Ab4d.SharpEngine.glTF;
using Ab4d.SharpEngine.Lights;
using Ab4d.SharpEngine.Materials;
using Ab4d.SharpEngine.Meshes;
using Ab4d.SharpEngine.SceneNodes;

namespace DvdLogoApp;

public sealed class TvModelStageControl : Grid
{
    private const int ScreenTextureWidth = 640;
    private const int ScreenTextureHeight = 424;
    private const string ScreenNodeName = "90sTV_mat_screen_0";

    private readonly SharpEngineSceneView sceneView;
    private readonly TextBlock statusText;
    private readonly DispatcherTimer initializationTimer;
    private byte[]? pendingScreenPixels;
    private byte[]? screenRenderBuffer;
    private GpuImage? screenTexture;
    private StandardMaterial? screenMaterial;
    private StandardMaterial? crtGlassMaterial;
    private byte[]? externalVideoPixels;
    private int externalVideoWidth;
    private int externalVideoHeight;
    private int externalVideoStride;
    private TargetPositionCamera? camera;
    private bool hasLoadedModel;
    private double crtCurve = 0.55;
    private double crtScanlines = 0.42;
    private double crtNoise = 0.05;
    private double crtGlitch;
    private double crtVignette = 0.52;
    private double crtFresnel = 0.58;
    private readonly DateTime crtShaderStartedAt = DateTime.UtcNow;
    private int crtShaderFrame;

    public TvModelStageControl()
    {
        Background = new SolidColorBrush(Color.FromRgb(7, 10, 15));

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
            Opacity = 0.72,
            IsHitTestVisible = false
        };

        Children.Add(sceneView);
        Children.Add(statusText);

        initializationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        initializationTimer.Tick += InitializationTimer_Tick;

        AttachedToVisualTree += (_, _) => initializationTimer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            initializationTimer.Stop();
            screenTexture?.Dispose();
            sceneView.Dispose();
        };
    }

    public void SetCamera(double heading, double attitude, double distance)
    {
        if (camera is null)
        {
            return;
        }

        camera.Heading = (float)heading;
        camera.Attitude = (float)attitude;
        camera.Distance = (float)distance;
        sceneView.RenderScene(forceUpdate: true, forceRender: true);
    }

    public void ResetCamera()
    {
        SetCamera(0, -2, 3.25);
    }

    public void SetScreenGlassOpacity(double opacity)
    {
        if (crtGlassMaterial is null)
        {
            return;
        }

        crtGlassMaterial.Opacity = (float)Math.Clamp(opacity, 0, 1);
        crtGlassMaterial.Update();
        sceneView.RenderScene(forceUpdate: true, forceRender: true);
    }

    public void SetCrtEffectSettings(
        double curve,
        double scanlines,
        double noise,
        double glitch,
        double vignette,
        double fresnel)
    {
        crtCurve = Math.Clamp(curve, 0, 1);
        crtScanlines = Math.Clamp(scanlines, 0, 1);
        crtNoise = Math.Clamp(noise, 0, 1);
        crtGlitch = Math.Clamp(glitch, 0, 1);
        crtVignette = Math.Clamp(vignette, 0, 1);
        crtFresnel = Math.Clamp(fresnel, 0, 1);
    }

    // Replaces the DVD screensaver image with the latest external input frame.
    public void SetExternalVideoFrame(byte[] pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < height * stride)
        {
            return;
        }

        // The caller returns decoder buffers to the pool after this call.
        // Retain an owned copy for later effect changes and camera redraws.
        var byteCount = checked(height * stride);
        if (externalVideoPixels is null || externalVideoPixels.Length != byteCount)
        {
            externalVideoPixels = new byte[byteCount];
        }
        Buffer.BlockCopy(pixels, 0, externalVideoPixels, 0, byteCount);
        externalVideoWidth = width;
        externalVideoHeight = height;
        externalVideoStride = stride;
    }

    // Returns the TV to its built-in DVD screensaver source.
    public void ClearExternalVideoFrame()
    {
        externalVideoPixels = null;
        externalVideoWidth = 0;
        externalVideoHeight = 0;
        externalVideoStride = 0;
    }

    public void UpdateScreenTexture(
        byte[]? logoPixels,
        int logoWidth,
        int logoHeight,
        int logoStride,
        double logoX,
        double logoY,
        double logoWidthOnStage,
        double logoHeightOnStage,
        double stageWidth,
        double stageHeight,
        double logoOpacity,
        double powerOffOpacity,
        double flickerOpacity)
    {
        pendingScreenPixels = RenderScreenPixels(
            logoPixels,
            logoWidth,
            logoHeight,
            logoStride,
            logoX,
            logoY,
            logoWidthOnStage,
            logoHeightOnStage,
            stageWidth,
            stageHeight,
            logoOpacity,
            powerOffOpacity,
            flickerOpacity,
            crtCurve,
            crtScanlines,
            crtNoise,
            crtGlitch,
            crtVignette,
            crtFresnel);

        UploadPendingScreenTexture();
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
            scene.Update(true);
            InstallLiveScreenMaterial(tvModel);

            sceneViewState.BackgroundColor = new Color4(0.027f, 0.039f, 0.059f, 1f);
            camera = new TargetPositionCamera("TV debug camera")
            {
                TargetPosition = new Vector3(0, 0.15f, 0),
                Distance = 3.25f,
                Heading = 0,
                Attitude = -2,
                FieldOfView = 32
            };
            sceneViewState.Camera = camera;

            scene.Update(true);
            sceneView.RenderScene(forceUpdate: true, forceRender: true);
            statusText.Text = "Made by Oleksandr Aryku";
            UploadPendingScreenTexture();
        }
        catch (Exception exception)
        {
            statusText.Text = $"3D stage could not start: {exception.Message}";
        }
    }

    private void InstallLiveScreenMaterial(GroupNode tvModel)
    {
        var screenNode = FindScreenNode(tvModel);

        if (screenNode is null || sceneView.GpuDevice is null)
        {
            statusText.Text = "3D screen mesh was not found.";
            return;
        }

        var initialPixels = pendingScreenPixels ?? RenderScreenPixels(
            logoPixels: null,
            logoWidth: 0,
            logoHeight: 0,
            logoStride: 0,
            logoX: 0,
            logoY: 0,
            logoWidthOnStage: 0,
            logoHeightOnStage: 0,
            stageWidth: ScreenTextureWidth,
            stageHeight: ScreenTextureHeight,
            logoOpacity: 0,
            powerOffOpacity: 1,
            flickerOpacity: 0,
            curve: crtCurve,
            scanlines: crtScanlines,
            noiseStrength: crtNoise,
            glitchStrength: crtGlitch,
            vignetteStrength: crtVignette,
            fresnelStrength: crtFresnel);
        var imageData = new RawImageData(
            ScreenTextureWidth,
            ScreenTextureHeight,
            ScreenTextureWidth * 4,
            StandardBitmapFormats.Bgra,
            initialPixels,
            false);

        screenMaterial = new StandardMaterial("Live DVD screen")
        {
            DiffuseColor = new Color3(1f, 1f, 1f),
            // The generated pixels already contain the CRT brightness. A low
            // emission preserves their dark colour instead of washing the image out.
            EmissiveColor = new Color3(0.045f, 0.055f, 0.06f),
            IsTwoSided = true,
            SpecularPower = 28
        };
        screenTexture = screenMaterial.CreateDiffuseTexture(
            sceneView.GpuDevice,
            imageData,
            CommonSamplerTypes.Clamp,
            "Live DVD screen texture",
            alphaClipThreshold: 0);

        var screenBounds = screenNode.GetLocalBoundingBox(updateIfDirty: true);
        var screenSize = screenBounds.Maximum - screenBounds.Minimum;
        var screenCenter = (screenBounds.Minimum + screenBounds.Maximum) * 0.5f;

        // The bounds are local to the imported screen mesh, while the overlay is
        // attached to its parent. Convert the overlay geometry between those spaces
        // before adding it, otherwise it floats when the camera moves around the TV.
        var screenParent = screenNode.Parent ?? tvModel;
        var screenWorldMatrix = screenNode.WorldMatrix;
        if (!Matrix4x4.Invert(screenParent.WorldMatrix, out var parentInverse))
        {
            statusText.Text = "3D TV screen transform could not be calculated.";
            return;
        }

        // Remove the imported screen mesh. Its dark texture is not transparent;
        // only its opening and transform are used to place our replacement surfaces.
        screenParent.Remove(screenNode);
        screenNode.Dispose();

        var localNormal = new Vector3(0, -1, 0);
        var worldNormal = Vector3.TransformNormal(localNormal, screenWorldMatrix);
        var parentNormal = Vector3.Normalize(Vector3.TransformNormal(worldNormal, parentInverse));
        Vector3 ToParentSpace(Vector3 position)
        {
            var worldPosition = Vector3.Transform(position, screenWorldMatrix);
            return Vector3.Transform(worldPosition, parentInverse);
        }

        var halfWidth = screenSize.X * 0.4925f;
        var halfHeight = screenSize.Z * 0.4925f;
        var liveScreenVertices = CreateScreenVertices(
            screenCenter,
            halfWidth,
            halfHeight,
            screenBounds.Maximum.Y + 0.001f,
            parentNormal,
            ToParentSpace);
        var screenMesh = new StandardMesh(
            liveScreenVertices,
            new[] { 0, 1, 2, 0, 2, 3 },
            BoundingBox.FromVertices(liveScreenVertices),
            "Live DVD screen mesh");
        var liveScreenPlane = new MeshModelNode(screenMesh, screenMaterial, "Live DVD screen")
        {
            BackMaterial = screenMaterial
        };
        screenParent.Add(liveScreenPlane);

        crtGlassMaterial = new StandardMaterial("Simulated CRT glass")
        {
            DiffuseColor = new Color3(0.035f, 0.04f, 0.042f),
            EmissiveColor = new Color3(0.002f, 0.002f, 0.002f),
            Opacity = 0.1f,
            HasTransparency = true,
            IsTwoSided = true,
            SpecularPower = 96
        };
        var glassVertices = CreateScreenVertices(
            screenCenter,
            halfWidth,
            halfHeight,
            screenBounds.Maximum.Y - 0.004f,
            parentNormal,
            ToParentSpace);
        var glassMesh = new StandardMesh(
            glassVertices,
            new[] { 0, 1, 2, 0, 2, 3 },
            BoundingBox.FromVertices(glassVertices),
            "Simulated CRT glass mesh");
        var glassPlane = new MeshModelNode(glassMesh, crtGlassMaterial, "Simulated CRT glass")
        {
            BackMaterial = crtGlassMaterial
        };
        screenParent.Add(glassPlane);
        liveScreenPlane.Update();
        glassPlane.Update();
    }

    private static PositionNormalTextureVertex[] CreateScreenVertices(
        Vector3 screenCenter,
        float halfWidth,
        float halfHeight,
        float localDepth,
        Vector3 normal,
        Func<Vector3, Vector3> toParentSpace)
    {
        return new[]
        {
            new PositionNormalTextureVertex(
                toParentSpace(new Vector3(screenCenter.X - halfWidth, localDepth, screenCenter.Z + halfHeight)),
                normal,
                new Vector2(0, 0)),
            new PositionNormalTextureVertex(
                toParentSpace(new Vector3(screenCenter.X + halfWidth, localDepth, screenCenter.Z + halfHeight)),
                normal,
                new Vector2(1, 0)),
            new PositionNormalTextureVertex(
                toParentSpace(new Vector3(screenCenter.X + halfWidth, localDepth, screenCenter.Z - halfHeight)),
                normal,
                new Vector2(1, 1)),
            new PositionNormalTextureVertex(
                toParentSpace(new Vector3(screenCenter.X - halfWidth, localDepth, screenCenter.Z - halfHeight)),
                normal,
                new Vector2(0, 1))
        };
    }

    private static ModelNode? FindScreenNode(GroupNode tvModel)
    {
        var screenNode = tvModel.GetChild<ModelNode>(ScreenNodeName, int.MaxValue);

        if (screenNode is not null)
        {
            return screenNode;
        }

        ModelNode? matchingNode = null;
        tvModel.ForEachChild<ModelNode>(node =>
        {
            if (matchingNode is null
                && node.Name.Contains("screen", StringComparison.OrdinalIgnoreCase))
            {
                matchingNode = node;
            }
        });

        return matchingNode;
    }

    private void UploadPendingScreenTexture()
    {
        if (screenTexture is null || pendingScreenPixels is null)
        {
            return;
        }

        screenTexture.CopyDataToImage(
            pendingScreenPixels,
            transitionImageToShaderReadOnlyOptimalLayout: true);
        screenMaterial?.Update();
        sceneView.RenderScene(forceUpdate: true, forceRender: true);
    }

    private byte[] RenderScreenPixels(
        byte[]? logoPixels,
        int logoWidth,
        int logoHeight,
        int logoStride,
        double logoX,
        double logoY,
        double logoWidthOnStage,
        double logoHeightOnStage,
        double stageWidth,
        double stageHeight,
        double logoOpacity,
        double powerOffOpacity,
        double flickerOpacity,
        double curve,
        double scanlines,
        double noiseStrength,
        double glitchStrength,
        double vignetteStrength,
        double fresnelStrength)
    {
        screenRenderBuffer ??= new byte[ScreenTextureWidth * ScreenTextureHeight * 4];
        var pixels = screenRenderBuffer;
        var safeStageWidth = stageWidth > 0 ? stageWidth : ScreenTextureWidth;
        var safeStageHeight = stageHeight > 0 ? stageHeight : ScreenTextureHeight;

        if (externalVideoPixels is not null)
        {
            DrawExternalFrame(
                pixels,
                externalVideoPixels,
                externalVideoWidth,
                externalVideoHeight,
                externalVideoStride);
            ApplyFastExternalCrtShader(
                pixels,
                curve,
                scanlines,
                noiseStrength,
                glitchStrength,
                vignetteStrength,
                fresnelStrength,
                (DateTime.UtcNow - crtShaderStartedAt).TotalSeconds,
                ++crtShaderFrame);
        }
        else
        {
            FillScreensaverBackground(pixels);
            DrawLogo(
                pixels,
                logoPixels,
                logoWidth,
                logoHeight,
                logoStride,
                logoX,
                logoY,
                logoWidthOnStage,
                logoHeightOnStage,
                safeStageWidth,
                safeStageHeight,
                logoOpacity);
            ApplyCrtShader(
                pixels,
                curve,
                scanlines,
                noiseStrength,
                glitchStrength,
                vignetteStrength,
                fresnelStrength,
                (DateTime.UtcNow - crtShaderStartedAt).TotalSeconds,
                ++crtShaderFrame);
        }
        BlendSolid(pixels, 255, 255, 255, Math.Clamp(flickerOpacity, 0, 1));
        BlendSolid(pixels, 2, 3, 3, Math.Clamp(powerOffOpacity, 0, 1));

        return pixels;
    }

    // External video already arrives fitted to the screen. This keeps the same
    // CRT controls but avoids per-pixel square roots, powers, and trigonometry
    // on every live frame, which would make the audio clock outrun the display.
    private static void ApplyFastExternalCrtShader(
        byte[] pixels,
        double curve,
        double scanlines,
        double noiseStrength,
        double glitchStrength,
        double vignetteStrength,
        double fresnelStrength,
        double time,
        int frame)
    {
        if (curve <= 0 && scanlines <= 0 && noiseStrength <= 0 && glitchStrength <= 0 && vignetteStrength <= 0 && fresnelStrength <= 0)
        {
            return;
        }

        var frameNoise = frame % 1000;
        Parallel.For(0, ScreenTextureHeight, y =>
        {
            var normalizedY = (double)y / Math.Max(1, ScreenTextureHeight - 1);
            var scan = (0.5 + (0.5 * Math.Sin((4 * time) + (normalizedY * ScreenTextureHeight * 1.75)))) * scanlines;
            var centeredY = normalizedY * 2 - 1;

            for (var x = 0; x < ScreenTextureWidth; x++)
            {
                var normalizedX = (double)x / Math.Max(1, ScreenTextureWidth - 1);
                var centeredX = normalizedX * 2 - 1;
                // Match the screensaver aperture, cancelling the radial square roots.
                var denominator = Math.Max(0.0001, 1 - 0.1 * curve * (centeredX * centeredX + centeredY * centeredY));
                var warpedU = 0.5 + centeredX / denominator * 0.5;
                var warpedV = 0.5 + centeredY / denominator * 0.5;
                var index = ((y * ScreenTextureWidth) + x) * 4;
                if (warpedU < 0 || warpedU > 1 || warpedV < 0 || warpedV > 1)
                {
                    pixels[index] = 0;
                    pixels[index + 1] = 0;
                    pixels[index + 2] = 0;
                    continue;
                }
                var edge = Math.Min(Math.Min(warpedU, 1 - warpedU), Math.Min(warpedV, 1 - warpedV));
                var fresnelEdge = 1 - Math.Clamp(edge * 5, 0, 1);
                var fresnel = fresnelEdge * fresnelEdge * fresnelEdge * fresnelEdge * fresnelEdge * fresnelStrength;
                var vignetteShape = Math.Clamp(1 - (Math.Abs(normalizedX - 0.5) * 1.65 + Math.Abs(normalizedY - 0.5) * 1.85), 0, 1);
                var vignette = (1 - vignetteShape) * vignetteStrength;
                var grain = (ShaderNoise(x, y, frameNoise) - 0.5) * noiseStrength;
                var glitch = glitchStrength <= 0
                    ? 0
                    : Math.Clamp((Math.Sin((normalizedY + (time * 2.4)) * 22) - 0.992) * 120 * glitchStrength, 0, 1);
                var overlayAlpha = Math.Clamp((scan * 0.18) + (Math.Abs(grain) * 0.64) + (vignette * 0.62) + (fresnel * 0.45) + (glitch * 0.28), 0, 0.92);
                var overlayRed = (fresnel * 190) + (grain > 0 ? grain * 255 : 0) + (glitch * 220);
                var overlayGreen = (fresnel * 235) + (grain > 0 ? grain * 255 : 0) + (glitch * 28);
                var overlayBlue = (fresnel * 255) + (grain > 0 ? grain * 255 : 0) + (glitch * 15);
                var darken = Math.Clamp((scan * 0.35) + (vignette * 0.7), 0, 1);
                var baseBlue = pixels[index] * (1 - darken);
                var baseGreen = pixels[index + 1] * (1 - darken);
                var baseRed = pixels[index + 2] * (1 - darken);

                pixels[index] = (byte)Math.Clamp(Math.Round((baseBlue * (1 - overlayAlpha)) + (overlayBlue * overlayAlpha)), 0, 255);
                pixels[index + 1] = (byte)Math.Clamp(Math.Round((baseGreen * (1 - overlayAlpha)) + (overlayGreen * overlayAlpha)), 0, 255);
                pixels[index + 2] = (byte)Math.Clamp(Math.Round((baseRed * (1 - overlayAlpha)) + (overlayRed * overlayAlpha)), 0, 255);
            }
        });
    }

    private static void FillScreensaverBackground(byte[] pixels)
    {
        for (var y = 0; y < ScreenTextureHeight; y++)
        {
            var v = (double)y / Math.Max(1, ScreenTextureHeight - 1);

            for (var x = 0; x < ScreenTextureWidth; x++)
            {
                var u = (double)x / Math.Max(1, ScreenTextureWidth - 1);
                var index = ((y * ScreenTextureWidth) + x) * 4;
                var vignette = Math.Clamp(
                    1.0 - (Math.Pow(Math.Abs(u - 0.5) * 1.55, 2) + Math.Pow(Math.Abs(v - 0.5) * 1.75, 2)),
                    0.34,
                    1.0);
                var scanline = y % 4 == 0 ? 0.82 : 1.0;
                var blue = (10 + (22 * u) + (18 * v)) * vignette * scanline;
                var green = (11 + (18 * u) + (22 * v)) * vignette * scanline;
                var red = (6 + (10 * u) + (12 * v)) * vignette * scanline;

                pixels[index] = (byte)Math.Clamp(Math.Round(blue), 0, 255);
                pixels[index + 1] = (byte)Math.Clamp(Math.Round(green), 0, 255);
                pixels[index + 2] = (byte)Math.Clamp(Math.Round(red), 0, 255);
                pixels[index + 3] = 255;
            }
        }
    }

    // This is the existing CRT shader's animated overlay, rendered into the same
    // texture used by the 3D screen plane so it remains fitted to the model.
    private static void ApplyCrtShader(
        byte[] pixels,
        double curve,
        double scanlines,
        double noiseStrength,
        double glitchStrength,
        double vignetteStrength,
        double fresnelStrength,
        double time,
        int frame)
    {
        if (curve <= 0 && scanlines <= 0 && noiseStrength <= 0 && glitchStrength <= 0 && vignetteStrength <= 0 && fresnelStrength <= 0)
        {
            return;
        }

        var frameNoise = frame % 1000;
        Parallel.For(0, ScreenTextureHeight, y =>
        {
            for (var x = 0; x < ScreenTextureWidth; x++)
            {
                var u = (double)x / Math.Max(1, ScreenTextureWidth - 1);
                var v = (double)y / Math.Max(1, ScreenTextureHeight - 1);
                var centeredX = (u * 2) - 1;
                var centeredY = (v * 2) - 1;
                var radius = Math.Sqrt((centeredX * centeredX) + (centeredY * centeredY));
                var warpedRadius = radius / Math.Max(0.0001, 1 - (0.1 * curve * radius * radius));
                var warpedU = 0.5 + (warpedRadius * centeredX / Math.Max(radius, 0.0001) * 0.5);
                var warpedV = 0.5 + (warpedRadius * centeredY / Math.Max(radius, 0.0001) * 0.5);
                var index = ((y * ScreenTextureWidth) + x) * 4;

                if (warpedU < 0 || warpedU > 1 || warpedV < 0 || warpedV > 1)
                {
                    pixels[index] = 0;
                    pixels[index + 1] = 0;
                    pixels[index + 2] = 0;
                    continue;
                }

                var edgeDistance = Math.Min(Math.Min(warpedU, 1 - warpedU), Math.Min(warpedV, 1 - warpedV));
                var fresnel = Math.Pow(1 - Math.Clamp(edgeDistance * 5, 0, 1), 5) * fresnelStrength;
                var vig = 8 * warpedU * warpedV * (1 - warpedU) * (1 - warpedV);
                var vignette = (1 - Math.Clamp(Math.Pow(vig, 0.25), 0, 1)) * vignetteStrength;
                var scan = (0.5 + (0.5 * Math.Sin((4 * time) + (warpedV * ScreenTextureHeight * 1.75)))) * scanlines;
                var grain = (ShaderNoise(x, y, frameNoise) - 0.5) * noiseStrength;
                var glitchBand = Math.Max(0, Math.Sin((warpedV + (time * 2.4)) * 22) - (0.992 - glitchStrength * 0.16));
                var glitch = Math.Clamp(glitchBand * 120 * glitchStrength, 0, 1);
                var overlayAlpha = Math.Clamp((scan * 0.18) + (Math.Abs(grain) * 0.64) + (vignette * 0.62) + (fresnel * 0.45) + (glitch * 0.28), 0, 0.92);
                var overlayRed = (fresnel * 190) + (grain > 0 ? grain * 255 : 0) + (glitch * 220);
                var overlayGreen = (fresnel * 235) + (grain > 0 ? grain * 255 : 0) + (glitch * 28);
                var overlayBlue = (fresnel * 255) + (grain > 0 ? grain * 255 : 0) + (glitch * 15);
                var darken = Math.Clamp((scan * 0.35) + (vignette * 0.7), 0, 1);
                var baseBlue = pixels[index] * (1 - darken);
                var baseGreen = pixels[index + 1] * (1 - darken);
                var baseRed = pixels[index + 2] * (1 - darken);

                pixels[index] = (byte)Math.Clamp(Math.Round((baseBlue * (1 - overlayAlpha)) + (overlayBlue * overlayAlpha)), 0, 255);
                pixels[index + 1] = (byte)Math.Clamp(Math.Round((baseGreen * (1 - overlayAlpha)) + (overlayGreen * overlayAlpha)), 0, 255);
                pixels[index + 2] = (byte)Math.Clamp(Math.Round((baseRed * (1 - overlayAlpha)) + (overlayRed * overlayAlpha)), 0, 255);
            }
        });
    }

    private static double ShaderNoise(int x, int y, int frame)
    {
        var value = unchecked((uint)(x * 374761393 + y * 668265263 + frame * 69069));
        value = (value ^ (value >> 13)) * 1274126177u;
        return ((value ^ (value >> 16)) & 0xFFFF) / 65535.0;
    }

    // Fits an incoming BGRA frame into the TV's fixed screen texture.
    private static void DrawExternalFrame(
        byte[] destination,
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride)
    {
        if (sourceWidth == ScreenTextureWidth
            && sourceHeight == ScreenTextureHeight
            && sourceStride == ScreenTextureWidth * 4
            && source.Length >= destination.Length)
        {
            Buffer.BlockCopy(source, 0, destination, 0, destination.Length);
            return;
        }

        for (var y = 0; y < ScreenTextureHeight; y++)
        {
            var sourceY = Math.Clamp((int)((double)y / ScreenTextureHeight * sourceHeight), 0, sourceHeight - 1);
            var sourceRow = sourceY * sourceStride;

            for (var x = 0; x < ScreenTextureWidth; x++)
            {
                var sourceX = Math.Clamp((int)((double)x / ScreenTextureWidth * sourceWidth), 0, sourceWidth - 1);
                var sourceIndex = sourceRow + (sourceX * 4);
                var destinationIndex = ((y * ScreenTextureWidth) + x) * 4;

                destination[destinationIndex] = source[sourceIndex];
                destination[destinationIndex + 1] = source[sourceIndex + 1];
                destination[destinationIndex + 2] = source[sourceIndex + 2];
                destination[destinationIndex + 3] = 255;
            }
        }
    }

    private static void DrawLogo(
        byte[] destination,
        byte[]? logoPixels,
        int logoWidth,
        int logoHeight,
        int logoStride,
        double logoX,
        double logoY,
        double logoWidthOnStage,
        double logoHeightOnStage,
        double stageWidth,
        double stageHeight,
        double logoOpacity)
    {
        if (logoPixels is null
            || logoWidth <= 0
            || logoHeight <= 0
            || logoStride <= 0
            || logoWidthOnStage <= 0
            || logoHeightOnStage <= 0
            || logoOpacity <= 0)
        {
            return;
        }

        var destinationX = (int)Math.Round(logoX / stageWidth * ScreenTextureWidth);
        var destinationY = (int)Math.Round(logoY / stageHeight * ScreenTextureHeight);
        var destinationWidth = Math.Max(1, (int)Math.Round(logoWidthOnStage / stageWidth * ScreenTextureWidth));
        var destinationHeight = Math.Max(1, (int)Math.Round(logoHeightOnStage / stageHeight * ScreenTextureHeight));

        for (var y = 0; y < destinationHeight; y++)
        {
            var targetY = destinationY + y;

            if (targetY < 0 || targetY >= ScreenTextureHeight)
            {
                continue;
            }

            var sourceY = Math.Clamp((int)((double)y / destinationHeight * logoHeight), 0, logoHeight - 1);
            var sourceRow = sourceY * logoStride;

            for (var x = 0; x < destinationWidth; x++)
            {
                var targetX = destinationX + x;

                if (targetX < 0 || targetX >= ScreenTextureWidth)
                {
                    continue;
                }

                var sourceX = Math.Clamp((int)((double)x / destinationWidth * logoWidth), 0, logoWidth - 1);
                var sourceIndex = sourceRow + (sourceX * 4);
                var alpha = Math.Clamp((logoPixels[sourceIndex + 3] / 255.0) * logoOpacity, 0, 1);

                if (alpha <= 0)
                {
                    continue;
                }

                var targetIndex = ((targetY * ScreenTextureWidth) + targetX) * 4;
                destination[targetIndex] = BlendByte(destination[targetIndex], logoPixels[sourceIndex], alpha);
                destination[targetIndex + 1] = BlendByte(destination[targetIndex + 1], logoPixels[sourceIndex + 1], alpha);
                destination[targetIndex + 2] = BlendByte(destination[targetIndex + 2], logoPixels[sourceIndex + 2], alpha);
            }
        }
    }

    private static void BlendSolid(byte[] pixels, byte red, byte green, byte blue, double opacity)
    {
        if (opacity <= 0)
        {
            return;
        }

        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = BlendByte(pixels[index], blue, opacity);
            pixels[index + 1] = BlendByte(pixels[index + 1], green, opacity);
            pixels[index + 2] = BlendByte(pixels[index + 2], red, opacity);
        }
    }

    private static byte BlendByte(byte destination, byte source, double alpha)
    {
        return (byte)Math.Clamp(Math.Round((source * alpha) + (destination * (1 - alpha))), 0, 255);
    }
}
