using System;
using Avalonia;

namespace DvdLogoApp;

internal static class Program
{
    [STAThread]
    // Starts the app and hands control over to Avalonia's desktop lifetime.
    public static void Main(string[] args)
    {
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
