using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class DemoScenarioTests
{
    private static readonly DemoRequest Request = new("192.0.2.10", "homelab", DemoBehavior.Success);

    [Theory]
    [InlineData("nas.local", "homelab", true)]
    [InlineData("192.0.2.10", "user", true)]
    [InlineData("::1", "user", true)]
    [InlineData("https://nas", "user", false)]
    [InlineData("nas; reboot", "user", false)]
    [InlineData("", "user", false)]
    [InlineData("nas", "", false)]
    [InlineData("nas", "user\nsecret", false)]
    public void ValidatesExampleAddressAndUser(string host, string user, bool valid) =>
        Assert.Equal(valid, new DemoRequest(host, user, DemoBehavior.Success).IsValid);

    [Fact]
    public async Task WaitsBeforePairingAndCompletesInOrder()
    {
        var adapter = new Adapter(); var observer = new Observer(); var confirmation = new Confirmation();
        var run = new DemoScenario(adapter).RunAsync(Request, observer, confirmation, TestContext.Current.CancellationToken);
        await confirmation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(3, adapter.Calls.Count);
        Assert.Equal(new WizardProgress(WizardState.WaitingForUser, WizardOperation.PairDevice, 3, 6), observer.Updates[^1]);
        Assert.False(run.IsCompleted);
        confirmation.Allow.TrySetResult();
        var result = await run;
        Assert.Equal(WizardState.Succeeded, result.State); Assert.Equal(6, result.Completed);
        Assert.Equal(Enum.GetValues<WizardOperation>(), adapter.Calls);
        Assert.Equal(Enumerable.Range(0, 6), observer.Updates.Where(x => x.State == WizardState.Running).Select(x => x.Completed));
    }

    [Theory]
    [InlineData(false, WizardState.Failed)]
    [InlineData(true, WizardState.OutcomeUnknown)]
    public async Task FailureOrUnknownStopsWithoutRetry(bool unknown, WizardState expected)
    {
        var adapter = new Adapter
        {
            Result = operation => operation == WizardOperation.ConfigureMedia
            ? new(new("demo.fixture", "Ошибка", "Безопасная диагностика"), unknown) : new()
        };
        var result = await new DemoScenario(adapter).RunAsync(Request, new Observer(), new Confirmation(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Equal(1, result.Completed);
        Assert.Equal([WizardOperation.PrepareFolders, WizardOperation.ConfigureMedia], adapter.Calls);
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotDispatchPairingAndReleasesSession()
    {
        var adapter = new Adapter(); var scenario = new DemoScenario(adapter); var confirmation = new Confirmation();
        using var cancellation = new CancellationTokenSource();
        var run = scenario.RunAsync(Request, new Observer(), confirmation, cancellation.Token);
        await confirmation.Entered.Task; cancellation.Cancel();
        var result = await run;
        Assert.Equal(WizardState.Cancelled, result.State); Assert.Equal(3, result.Completed);
        Assert.DoesNotContain(WizardOperation.PairDevice, adapter.Calls);
        var next = new Confirmation(); next.Allow.SetResult();
        Assert.Equal(WizardState.Succeeded, (await scenario.RunAsync(Request, new Observer(), next, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task PreCancelledRunDispatchesNothing()
    {
        var adapter = new Adapter(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await new DemoScenario(adapter).RunAsync(Request, new Observer(), new Confirmation(), cancellation.Token);
        Assert.Equal(WizardState.Cancelled, result.State); Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task RejectsConcurrentRunWhileFirstWaits()
    {
        var adapter = new Adapter(); var scenario = new DemoScenario(adapter); var confirmation = new Confirmation();
        var first = scenario.RunAsync(Request, new Observer(), confirmation, TestContext.Current.CancellationToken);
        await confirmation.Entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunAsync(Request, new Observer(), new Confirmation(), TestContext.Current.CancellationToken));
        Assert.Equal(3, adapter.Calls.Count);
        confirmation.Allow.SetResult(); Assert.Equal(WizardState.Succeeded, (await first).State);
    }

    [Fact]
    public async Task AdapterExceptionDoesNotExposeCredentials()
    {
        var adapter = new Adapter { Result = _ => throw new InvalidOperationException("password=fixture-secret") };
        var observer = new Observer();
        var result = await new DemoScenario(adapter).RunAsync(Request, observer, new Confirmation(), TestContext.Current.CancellationToken);
        Assert.Equal(WizardState.Failed, result.State);
        Assert.All(observer.Updates, update => Assert.DoesNotContain("fixture-secret", update.ToString()));
        Assert.Equal("demo.unexpected", result.Problem?.Code);
    }

    private sealed class Adapter : IDemoInstallationAdapter
    {
        public List<WizardOperation> Calls { get; } = [];
        public Func<WizardOperation, DemoOperationResult> Result { get; init; } = _ => new();
        public Task<DemoOperationResult> ExecuteAsync(WizardOperation operation, DemoBehavior behavior, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add(operation); return Task.FromResult(Result(operation)); }
    }
    private sealed class Observer : IWizardObserver
    {
        public List<WizardProgress> Updates { get; } = [];
        public Task ReportAsync(WizardProgress progress) { Updates.Add(progress); return Task.CompletedTask; }
    }
    private sealed class Confirmation : IUserConfirmation
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Allow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync(CancellationToken token) { Entered.TrySetResult(); return Allow.Task.WaitAsync(token); }
    }
}
