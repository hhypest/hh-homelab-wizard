using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Desktop;
using HhHomelab.Wizard.Desktop.Services;
using HhHomelab.Wizard.Desktop.ViewModels;
using HhHomelab.Wizard.Desktop.Views;
using HhHomelab.Wizard.Domain;
using Xunit;

namespace HhHomelab.Wizard.DesktopTests;

public sealed class UiFixture : IAsyncLifetime
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(TestApp));
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseSkia();
}
[CollectionDefinition("UI", DisableParallelization = true)]
public sealed class UiCollection : ICollectionFixture<UiFixture>;

[Collection("UI")]
public sealed class FocusTests(UiFixture fixture)
{
    [Fact]
    public Task CompiledBindingsValidateInputAndNavigate() => fixture.Session.Dispatch(() =>
    {
        var (window, vm, _) = Create();
        try
        {
            window.FindControl<TextBox>("HostInput")!.Text = "https://nas";
            Click(window, "ContinueButton");
            Assert.True(vm.HasValidation); Assert.True(vm.IsConnection);
            window.FindControl<TextBox>("HostInput")!.Text = "nas.local";
            window.FindControl<TextBox>("UserInput")!.Text = "owner";
            Click(window, "ContinueButton");
            Assert.True(vm.IsPlan); Assert.Equal("owner", vm.User);
            Assert.True(window.FindControl<Button>("StartButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task ConfirmationAndRepeatedStartKeepOneSession() => DispatchAsync(async () =>
    {
        var (window, vm, adapter) = Create();
        try
        {
            Click(window, "ContinueButton");
            var run = vm.StartCommand.ExecuteAsync(null);
            await Until(() => vm.IsWaiting);
            Assert.Equal(3, adapter.Calls.Count);
            Assert.True(window.FindControl<Button>("ConfirmButton")!.IsEnabled);
            Assert.False(vm.BackCommand.CanExecute(null));
            vm.BackCommand.Execute(null); vm.NewDemoCommand.Execute(null);
            await vm.StartCommand.ExecuteAsync(null);
            Assert.True(vm.IsProgress); Assert.False(run.IsCompleted); Assert.Equal(3, adapter.Calls.Count);
            Click(window, "ConfirmButton"); await run;
            Assert.True(vm.IsResult); Assert.Equal(WizardState.Succeeded, vm.Progress!.State);
            Assert.Equal(6, adapter.Calls.Count); Assert.Equal(100, vm.ProgressPercent);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(DemoBehavior.Failure, WizardState.Failed, 2)]
    [InlineData(DemoBehavior.LostResponse, WizardState.OutcomeUnknown, 4)]
    public Task FailureAndUnknownDoNotContinue(DemoBehavior behavior, WizardState state, int count) => DispatchAsync(async () =>
    {
        var (window, vm, adapter) = Create(behavior);
        try
        {
            vm.ContinueCommand.Execute(null); var run = vm.StartCommand.ExecuteAsync(null);
            if (behavior == DemoBehavior.LostResponse) { await Until(() => vm.IsWaiting); vm.ConfirmCommand.Execute(null); }
            await run;
            Assert.True(vm.IsResult); Assert.Equal(state, vm.Progress!.State);
            Assert.Equal(count, adapter.Calls.Count); Assert.False(vm.StartCommand.CanExecute(null));
            Assert.DoesNotContain("fixture-secret", vm.Diagnostic);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task StopWhileWaitingAndLateConfirmationCannotResume() => DispatchAsync(async () =>
    {
        var (window, vm, adapter) = Create();
        try
        {
            vm.ContinueCommand.Execute(null); var run = vm.StartCommand.ExecuteAsync(null);
            await Until(() => vm.IsWaiting); Click(window, "StopButton"); await run;
            vm.ConfirmCommand.Execute(null);
            Assert.Equal(WizardState.Cancelled, vm.Progress!.State); Assert.Equal(3, adapter.Calls.Count);
            Assert.False(vm.IsBusy); Assert.True(vm.NewDemoCommand.CanExecute(null));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CloseCancelsAndAwaitsPendingOperation() => DispatchAsync(async () =>
    {
        var (window, vm, adapter) = Create(block: true);
        vm.ContinueCommand.Execute(null); var run = vm.StartCommand.ExecuteAsync(null);
        await adapter.Entered.Task;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); await run;
        Assert.Equal(WizardState.Cancelled, vm.Progress!.State); Assert.False(vm.IsBusy);
        Assert.False(vm.StartCommand.CanExecute(null)); Assert.False(window.IsVisible);
    });

    [Theory]
    [InlineData(640, 600, false)]
    [InlineData(1000, 860, true)]
    public Task LayoutFitsWindowAndUsesTheme(double width, double height, bool dark) => fixture.Session.Dispatch(() =>
    {
        var (window, vm, _) = Create(width: width, height: height);
        try
        {
            window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            window.SetRenderScaling(dark ? 1.5 : 1); window.UpdateLayout();
            foreach (var name in new[] { "HostInput", "UserInput", "ContinueButton" })
            {
                var control = window.FindControl<Control>(name)!;
                var location = control.TranslatePoint(default, window)!.Value;
                Assert.True(control.Bounds.Width > 0);
                Assert.InRange(location.X, 0, width);
                Assert.True(location.X + control.Bounds.Width <= width, $"{name} clips at {width}px");
            }
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
        }
        finally { window.Close(); }
    }, TestContext.Current.CancellationToken);

    private static (MainWindow Window, MainWindowViewModel Vm, Adapter Adapter) Create(DemoBehavior behavior = DemoBehavior.Success, bool block = false, double width = 1000, double height = 860)
    {
        var adapter = new Adapter { Block = block };
        var vm = new MainWindowViewModel(new DemoScenario(adapter), new AvaloniaUiDispatcher());
        vm.SelectedDemo = vm.DemoChoices.Single(x => x.Behavior == behavior);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height }; window.Show(); window.UpdateLayout();
        return (window, vm, adapter);
    }
    private static void Click(Window window, string name)
    {
        window.UpdateLayout();
        var button = window.FindControl<Button>(name)!; Assert.True(button.IsEnabled);
        button.BringIntoView(); window.UpdateLayout();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }
    private Task DispatchAsync(Func<Task> action) => fixture.Session.Dispatch(async () => { await action(); return true; }, TestContext.Current.CancellationToken);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class Adapter : IDemoInstallationAdapter
    {
        public bool Block { get; init; }
        public List<WizardOperation> Calls { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DemoOperationResult> ExecuteAsync(WizardOperation operation, DemoBehavior behavior, CancellationToken token)
        {
            Calls.Add(operation); Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, token);
            if (behavior == DemoBehavior.Failure && operation == WizardOperation.ConfigureMedia)
                return new(new("demo.fixture", "Ошибка Media", "Искусственная ошибка"));
            if (behavior == DemoBehavior.LostResponse && operation == WizardOperation.PairDevice)
                return new(new("demo.fixture", "Ответ потерян", "Повтор запрещён"), true);
            return new();
        }
    }
}
