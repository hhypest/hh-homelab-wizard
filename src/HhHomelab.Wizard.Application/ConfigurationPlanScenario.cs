using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

public sealed class OperationPlanMismatchException() : InvalidOperationException("operation.plan.mismatch");

/// <summary>Atomically pin a complete plan before the first step. A changed graph, specification,
/// identity or timeout must be rejected on resume. Binding is local metadata, not a NAS mutation.</summary>
public interface IOperationPlanJournal : IOperationJournal
{
    Task BindPlanAsync(ConfigurationOperationPlan plan, CancellationToken token);
}

public sealed record ConfigurationStepResult(Guid OperationId, OperationResult Result);

/// <summary>Only attempted steps are included; the final failure stops the whole mandatory plan.
/// Earlier successful steps remain visible when a later step is cancelled or uncertain.</summary>
public sealed record ConfigurationPlanResult(OperationResult Result, IReadOnlyList<ConfigurationStepResult> Steps);

/// <summary>Sequential dependency executor over the existing Observe/Apply/Verify scenario.
/// Every run reobserves completed steps; no in-memory completion flag permits skipping evidence.</summary>
public sealed class ConfigurationPlanScenario(IOperationPlanJournal journal, IOperationOwnership ownership)
{
    private int running;

    public async Task<ConfigurationPlanResult> ExecuteAsync(ConfigurationOperationPlan plan,
        IReadOnlyList<IConfigurationOperation> operations, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(operations);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Report(Failure(OperationOutcomeKind.Conflict, OperationErrorCode.OperationInProgress), []);
        var results = new List<ConfigurationStepResult>();
        try
        {
            token.ThrowIfCancellationRequested();
            // Copy and validate every binding before even persisting the plan. No partial execution
            // when a later adapter is missing or points at a different target/specification.
            var bindings = new Dictionary<Guid, IConfigurationOperation>();
            foreach (var operation in operations.ToArray())
            {
                if (operation is null || operation.Descriptor is not { IsValid: true } descriptor
                    || !bindings.TryAdd(descriptor.Identity.OperationId, operation))
                    return Report(Failure(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest), results);
            }
            if (bindings.Count != plan.Steps.Count || plan.Steps.Any(step =>
                !bindings.TryGetValue(step.Descriptor.Identity.OperationId, out var operation)
                || operation.Descriptor != step.Descriptor))
                return Report(Failure(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch), results);
            await journal.BindPlanAsync(plan, token).ConfigureAwait(false);
            var scenario = new ConfigurationOperationScenario(journal, ownership);
            foreach (var step in plan.ExecutionOrder)
            {
                token.ThrowIfCancellationRequested();
                var operation = bindings[step.Descriptor.Identity.OperationId];
                if (operation.Descriptor != step.Descriptor)
                    return Report(Failure(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch), results);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(step.Timeout);
                var result = await scenario.ExecuteAsync(operation, budget.Token).ConfigureAwait(false);
                // Pending writes already yield Unknown in the single-operation scenario. A budget
                // expiring before intent is a read/transport deadline, not caller cancellation.
                if (result.Kind == OperationOutcomeKind.Cancelled && !token.IsCancellationRequested && budget.IsCancellationRequested)
                    result = Failure(OperationOutcomeKind.Transport, OperationErrorCode.DeadlineExceeded);
                results.Add(new ConfigurationStepResult(step.Descriptor.Identity.OperationId, result));
                if (!result.IsSuccess)
                    return Report(result, results);
            }
            return Report(OperationResult.Completed(results.Any(step => step.Result.Kind == OperationOutcomeKind.Applied)), results);
        }
        catch (OperationPlanMismatchException)
        {
            return Report(Failure(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch), results);
        }
        catch (OperationLeaseUnavailableException)
        {
            return Report(Failure(OperationOutcomeKind.Conflict, OperationErrorCode.OperationInProgress), results);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Report(Failure(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled), results);
        }
        catch (Exception)
        {
            return Report(Failure(OperationOutcomeKind.Transport, OperationErrorCode.JournalUnavailable), results);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private static ConfigurationPlanResult Report(OperationResult result, List<ConfigurationStepResult> steps) =>
        new(result, Array.AsReadOnly(steps.ToArray()));

    private static OperationResult Failure(OperationOutcomeKind kind, OperationErrorCode code) => OperationResult.Failed(kind, code);
}
