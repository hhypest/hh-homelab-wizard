using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Desktop.Services;
using HhHomelab.Wizard.Desktop.ViewModels;
using HhHomelab.Wizard.Desktop.Views;
using HhHomelab.Wizard.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HhHomelab.Wizard.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IDemoInstallationAdapter, SimulatedInstallationAdapter>();
            services.AddSingleton<IDemoScenario, DemoScenario>();
            services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
            services.AddSingleton<MainWindowViewModel>();
            var provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
            );
            desktop.MainWindow = new MainWindow
            {
                DataContext = provider.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.Exit += (_, _) => provider.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
