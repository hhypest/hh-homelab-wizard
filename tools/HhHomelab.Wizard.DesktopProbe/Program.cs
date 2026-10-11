using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using HhHomelab.Wizard.Desktop;
using HhHomelab.Wizard.Desktop.ViewModels;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.DesktopProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<ProbeApp>().UsePlatformDetect()
        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
}

/// <summary>Developer-only native Win32/X11 check of the real composition root. Never connects to NAS.</summary>
public sealed class ProbeApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = lifetime.MainWindow!;
        window.Opened += async (_, _) =>
        {
            var exit = 1;
            var vm = (MainWindowViewModel)window.DataContext!;
            try
            {
                Directory.CreateDirectory("artifacts/native-desktop");
                window.RequestedThemeVariant = ThemeVariant.Light;
                await Task.Delay(150);
                Require(window.IsVisible && window.TryGetPlatformHandle() is not null, "native.window");
                Capture(window, "connection");
                vm.ContinueCommand.Execute(null); Require(vm.IsPlan, "navigate.plan"); Capture(window, "plan");
                var run = vm.StartCommand.ExecuteAsync(null);
                await Until(() => vm.IsWaiting); Capture(window, "confirmation");
                Require(!vm.StartCommand.CanExecute(null) && !vm.BackCommand.CanExecute(null), "session.guards");
                vm.ConfirmCommand.Execute(null); await run;
                Require(vm.Progress?.State == WizardState.Succeeded, "result.success"); Capture(window, "success");
                vm.NewDemoCommand.Execute(null); vm.ContinueCommand.Execute(null);
                var stopped = vm.StartCommand.ExecuteAsync(null);
                await Until(() => vm.IsWaiting); vm.StopCommand.Execute(null); await stopped;
                Require(vm.Progress?.State == WizardState.Cancelled, "result.cancelled");
                vm.NewDemoCommand.Execute(null); vm.SelectedDemo = vm.DemoChoices[2]; vm.ContinueCommand.Execute(null);
                var unknown = vm.StartCommand.ExecuteAsync(null);
                await Until(() => vm.IsWaiting); vm.ConfirmCommand.Execute(null); await unknown;
                Require(vm.Progress?.State == WizardState.OutcomeUnknown && !vm.StartCommand.CanExecute(null), "result.unknown");
                window.RequestedThemeVariant = ThemeVariant.Dark; Capture(window, "unknown-dark");
                await vm.CloseAsync(); Require(!vm.IsBusy, "close.completed");
                Console.WriteLine($"PASS native desktop: {window.TryGetPlatformHandle()?.HandleDescriptor}; success/cancel/unknown/close; {Environment.OSVersion.VersionString}");
                exit = 0;
            }
            catch (Exception) { Console.Error.WriteLine("FAIL native desktop probe; no NAS operations were performed."); }
            finally { await vm.CloseAsync(); lifetime.Shutdown(exit); }
        };
    }
    private static void Require(bool condition, string check)
    { if (!condition) throw new InvalidOperationException(check); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(window.Bounds.Size, window.RenderScaling), new Vector(96, 96) * window.RenderScaling);
        bitmap.Render(window);
        bitmap.Save(Path.Combine("artifacts/native-desktop", name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
