using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DvdLogoApp;

public partial class App : Application
{
    // Loads the Avalonia app markup from App.axaml.
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Creates the main window once Avalonia has finished starting up.
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
