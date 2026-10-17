using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

public enum DemoBehavior
{
    Success,
    Failure,
    LostResponse,
}

public sealed record DemoRequest(string Host, string User, DemoBehavior Behavior)
{
    public bool IsValid => HostValid && UserValid && Enum.IsDefined(Behavior);

    private bool HostValid =>
        !string.IsNullOrWhiteSpace(Host)
        && Host.Length <= 253
        && !Host.Any(char.IsWhiteSpace)
        && Uri.CheckHostName(Host) != UriHostNameType.Unknown;

    private bool UserValid =>
        !string.IsNullOrWhiteSpace(User) && User.Length <= 64 && !User.Any(char.IsControl);
}

public sealed record DemoOperationResult(
    WizardProblem? Problem = null,
    bool OutcomeUnknown = false
);

public interface IDemoInstallationAdapter
{
    Task<DemoOperationResult> ExecuteAsync(
        WizardOperation operation,
        DemoBehavior behavior,
        CancellationToken token
    );
}

public interface IWizardObserver
{
    Task ReportAsync(WizardProgress progress);
}

public interface IUserConfirmation
{
    Task WaitAsync(CancellationToken token);
}

public interface IDemoScenario
{
    Task<WizardProgress> RunAsync(
        DemoRequest request,
        IWizardObserver observer,
        IUserConfirmation confirmation,
        CancellationToken token
    );
}

/// <summary>M1 demonstration only. No installation plan, SSH, credentials or durable recovery.</summary>
public sealed class DemoScenario(IDemoInstallationAdapter adapter) : IDemoScenario
{
    private int running;

    public async Task<WizardProgress> RunAsync(
        DemoRequest request,
        IWizardObserver observer,
        IUserConfirmation confirmation,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!request.IsValid)
            throw new ArgumentException("demo.request.invalid", nameof(request));
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("demo.session.running");
        var completed = 0;
        var operation = WizardOperation.PrepareFolders;
        try
        {
            foreach (var next in Enum.GetValues<WizardOperation>())
            {
                operation = next;
                token.ThrowIfCancellationRequested();
                if (operation == WizardOperation.PairDevice)
                {
                    await observer
                        .ReportAsync(new(WizardState.WaitingForUser, operation, completed, 6))
                        .ConfigureAwait(false);
                    await confirmation.WaitAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                await observer
                    .ReportAsync(new(WizardState.Running, operation, completed, 6))
                    .ConfigureAwait(false);
                var result = await adapter
                    .ExecuteAsync(operation, request.Behavior, token)
                    .ConfigureAwait(false);
                if (result.OutcomeUnknown || result.Problem is not null)
                {
                    var failed = new WizardProgress(
                        result.OutcomeUnknown ? WizardState.OutcomeUnknown : WizardState.Failed,
                        operation,
                        completed,
                        6,
                        result.Problem
                    );
                    await observer.ReportAsync(failed).ConfigureAwait(false);
                    return failed;
                }
                completed++;
            }
            var success = new WizardProgress(WizardState.Succeeded, operation, completed, 6);
            await observer.ReportAsync(success).ConfigureAwait(false);
            return success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var cancelled = new WizardProgress(WizardState.Cancelled, operation, completed, 6);
            await observer.ReportAsync(cancelled).ConfigureAwait(false);
            return cancelled;
        }
        catch (Exception)
        {
            // Never forward adapter exception messages: a future transport may include credentials.
            var failed = new WizardProgress(
                WizardState.Failed,
                operation,
                completed,
                6,
                new(
                    "demo.unexpected",
                    "Демонстрация остановилась из-за непредвиденной ошибки.",
                    "Внутренняя ошибка демонстрационного сценария; подробности исключения скрыты."
                )
            );
            await observer.ReportAsync(failed).ConfigureAwait(false);
            return failed;
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }
}
