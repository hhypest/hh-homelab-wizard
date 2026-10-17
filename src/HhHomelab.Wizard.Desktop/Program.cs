using Avalonia;

namespace HhHomelab.Wizard.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        DesktopPlatform.Configure(AppBuilder.Configure<App>());
}
