using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

/// <summary>One bounded operation, not a graph executor or durable journal. Every previous intent
/// takes the observation-only reconciliation path; missing evidence never authorizes another Apply.</summary>
public sealed class ConfigurationOperationScenario(IOperationJournal journal, IOperationOwnership ownership)
{
    private int running;

    public async Task<OperationResult> ExecuteAsync(IConfigurationOperation operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(ownership);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Failure(OperationOutcomeKind.Conflict, OperationErrorCode.OperationInProgress);
        var state = new ExecutionState();
        try
        {
            token.ThrowIfCancellationRequested();
            var descriptor = operation.Descriptor;
            if (!descriptor.IsValid)
                return Failure(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest);
            await using var lease = await journal.AcquireAsync(descriptor, token).ConfigureAwait(false);
            return await ExecuteCoreAsync(operation, descriptor, state, token).ConfigureAwait(false);
        }
        catch (OperationLeaseUnavailableException) when (!state.HasIntent)
        {
            return Failure(OperationOutcomeKind.Conflict, OperationErrorCode.OperationInProgress);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !state.HasIntent)
        {
            return Failure(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled);
        }
        catch (Exception)
        {
            // Ports never leak raw exception text, endpoints or credentials to the presentation layer.
            return Failure(state.HasIntent ? OperationOutcomeKind.Unknown : OperationOutcomeKind.Transport, state.FailureCode);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private async Task<OperationResult> ExecuteCoreAsync(
        IConfigurationOperation operation,
        OperationDescriptor descriptor,
        ExecutionState state,
        CancellationToken token
    )
    {
        var entry = await journal.ReadAsync(descriptor, token).ConfigureAwait(false);
        state.HasIntent = entry is not null;
        if (entry is not null && !ValidEntry(entry, descriptor))
            return Failure(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch);
        state.FailureCode = state.HasIntent ? OperationErrorCode.WriteUncertain : OperationErrorCode.ReadFailed;
        var access = await CheckOwnershipAsync(descriptor, token).ConfigureAwait(false);
        if (access is not null)
            return UncertainIfPending(access, state.HasIntent);
        var read = await operation.ObserveAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return UncertainIfPending(read.Failure, state.HasIntent);
        var observed = read.Value!;
        if (!observed.IsValidFor(descriptor))
            return Failure(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch);
        if (observed.State == ObservedResourceState.Conflict)
            return Failure(OperationOutcomeKind.Conflict, OperationErrorCode.ResourceConflict);
        if (observed.State == ObservedResourceState.Satisfied)
            return await VerifyAndConfirmAsync(operation, descriptor, entry?.Confirmation is null ? entry?.Intent : null, observed, state, token).ConfigureAwait(false);
        if (entry is not null)
            return Failure(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain);
        return await ApplyFreshAsync(operation, descriptor, observed, state, token).ConfigureAwait(false);
    }

    private async Task<OperationResult> ApplyFreshAsync(
        IConfigurationOperation operation,
        OperationDescriptor descriptor,
        OperationObservation observed,
        ExecutionState state,
        CancellationToken token
    )
    {
        var access = await CheckOwnershipAsync(descriptor, token).ConfigureAwait(false);
        if (access is not null)
            return access;
        token.ThrowIfCancellationRequested();
        var intent = new OperationIntent(descriptor, observed);
        state.HasIntent = true;
        state.FailureCode = OperationErrorCode.JournalUnavailable;
        await journal.RecordIntentAsync(intent, token).ConfigureAwait(false);
        state.FailureCode = OperationErrorCode.WriteUncertain;
        token.ThrowIfCancellationRequested();
        access = await CheckOwnershipAsync(descriptor, token).ConfigureAwait(false);
        if (access is not null)
            return UncertainIfPending(access, true);
        var write = await operation.ApplyAsync(observed, token).ConfigureAwait(false);
        if (write.Failure is not null)
            return write.Failure;
        return await VerifyAndConfirmAsync(operation, descriptor, intent, null, state, token).ConfigureAwait(false);
    }

    private async Task<OperationResult> VerifyAndConfirmAsync(
        IConfigurationOperation operation,
        OperationDescriptor descriptor,
        OperationIntent? intent,
        OperationObservation? observed,
        ExecutionState state,
        CancellationToken token
    )
    {
        var access = await CheckOwnershipAsync(descriptor, token).ConfigureAwait(false);
        if (access is not null)
            return UncertainIfPending(access, state.HasIntent);
        var read = await operation.VerifyAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return UncertainIfPending(read.Failure, state.HasIntent);
        var verified = read.Value!;
        var revisionChanged = observed is not null && verified.Revision != observed.Revision;
        if (!verified.IsSatisfiedFor(descriptor) || revisionChanged)
            return Failure(state.HasIntent ? OperationOutcomeKind.Unknown : OperationOutcomeKind.Conflict, OperationErrorCode.VerificationFailed);
        access = await CheckOwnershipAsync(descriptor, token).ConfigureAwait(false);
        if (access is not null)
            return UncertainIfPending(access, state.HasIntent);
        if (intent is not null)
        {
            state.FailureCode = OperationErrorCode.JournalUnavailable;
            await journal.ConfirmAsync(intent, verified, token).ConfigureAwait(false);
        }
        return OperationResult.Completed(intent is not null);
    }

    private async Task<OperationResult?> CheckOwnershipAsync(OperationDescriptor descriptor, CancellationToken token)
    {
        var observed = await ownership.CheckAsync(descriptor.Identity.Resource, token).ConfigureAwait(false);
        return observed switch
        {
            ResourceOwnership.Owned => null,
            ResourceOwnership.Foreign => Failure(OperationOutcomeKind.Conflict, OperationErrorCode.OwnershipNotProven),
            _ => Failure(OperationOutcomeKind.Invalid, OperationErrorCode.OwnershipNotProven),
        };
    }

    private static bool ValidEntry(OperationJournalEntry entry, OperationDescriptor descriptor)
    {
        if (entry.Intent.Descriptor != descriptor || !entry.Intent.Baseline.IsValidFor(descriptor))
            return false;
        if (entry.Intent.Baseline.State != ObservedResourceState.Missing)
            return false;
        return entry.Confirmation is null || entry.Confirmation.IsSatisfiedFor(descriptor);
    }

    private static OperationResult UncertainIfPending(OperationResult result, bool pending) => pending
        ? Failure(OperationOutcomeKind.Unknown, result.Code)
        : result;

    private static OperationResult Failure(OperationOutcomeKind kind, OperationErrorCode code) => OperationResult.Failed(kind, code);

    private sealed class ExecutionState
    {
        public bool HasIntent { get; set; }

        public OperationErrorCode FailureCode { get; set; } = OperationErrorCode.JournalUnavailable;
    }
}
