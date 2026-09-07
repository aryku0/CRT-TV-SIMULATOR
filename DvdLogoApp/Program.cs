using System;
using Avalonia;
using Ab4d.SharpEngine;

namespace DvdLogoApp;

internal static class   Program
{
    [STAThread]
    // Starts the app and hands control over to Avalonia's desktop lifetime.
    public static void Main(string[] args)
    {
        Licensing.SetLicense(
            licenseOwner: "Personal Project",
            licenseType: "TrialLicense",
            license: "5B5D-FB34-443F-E977-BD9F-AC79-0452-7A2F-7280-BF40-6F7F-D267-DED1-1B8E-5F4D-C816-F368");

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Builds the Avalonia app object and enables desktop/platform services.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
    }
}
