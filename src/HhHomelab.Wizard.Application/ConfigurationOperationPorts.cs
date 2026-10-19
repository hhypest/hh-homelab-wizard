using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

public interface IConfigurationOperation
{
    OperationDescriptor Descriptor { get; }

    Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token);

    /// <summary>Recheck the baseline, then send at most one mutation. Rejected requires proof of
    /// no modification; ambiguous HTTP status, cancellation or loss after send must be Unknown.</summary>
    Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token);

    Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token);
}

/// <summary>Opt-in for an operation whose Apply is a conditional, no-clobber write of identical content:
/// it re-checks every precondition on the target before changing anything and can neither duplicate
/// nor overwrite a result. Only such an operation may be applied again when a pending intent meets a
/// fresh Missing observation; an in-flight earlier attempt then loses the race harmlessly.</summary>
public interface IRepeatableConditionalApply
{
}

public enum ResourceOwnership
{
    Owned,
    Foreign,
    Unproven,
}

/// <summary>Fresh marker/instance/profile checks tied to the actual transport target. An implementation
/// must not treat API login, matching names or a caller-supplied boolean as proof of ownership.</summary>
public interface IOperationOwnership
{
    Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token);
}

public sealed record OperationIntent(OperationDescriptor Descriptor, OperationObservation Baseline);

public sealed record OperationJournalEntry(OperationIntent Intent, OperationVerification? Confirmation = null);

public sealed class OperationLeaseUnavailableException() : InvalidOperationException("operation.lease.unavailable");

/// <summary>Mandatory execution boundary. Production implementation (#51) must atomically persist
/// intent before returning, reject a different descriptor, and hold exclusive installation/resource
/// locks through execution. Confirm is atomic and follows independent verification. No credentials.
/// The local file adapter supplies process-restart persistence; remote locks/ownership and host
/// durability prerequisites remain separate. There is no fallback in-memory implementation.</summary>
public interface IOperationJournal
{
    /// <summary>Acquire exclusive installation/resource ownership until disposal. Every method below
    /// must validate against this lease. Conflict is OperationLeaseUnavailableException, not lock theft.</summary>
    Task<IAsyncDisposable> AcquireAsync(OperationDescriptor descriptor, CancellationToken token);

    /// <summary>Null proves no intent exists in a valid, owned journal. A missing/corrupt/incompatible
    /// store is an error, never a new installation. An unresolved intent for the resource cannot be
    /// hidden by a new operation ID; return it or reject the conflict rather than returning null.</summary>
    Task<OperationJournalEntry?> ReadAsync(OperationDescriptor descriptor, CancellationToken token);

    Task RecordIntentAsync(OperationIntent intent, CancellationToken token);

    Task ConfirmAsync(OperationIntent intent, OperationVerification verification, CancellationToken token);
}
