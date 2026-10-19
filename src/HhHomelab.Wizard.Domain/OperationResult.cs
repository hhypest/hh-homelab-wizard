namespace HhHomelab.Wizard.Domain;

public enum OperationOutcomeKind
{
    Applied,
    AlreadySatisfied,
    Rejected,
    Unknown,
    Conflict,
    Transport,
    Invalid,
    Cancelled,
}

/// <summary>Safe machine codes. Text, diagnostics and suggested actions belong to Desktop.</summary>
public enum OperationErrorCode
{
    None,
    InvalidRequest,
    IdentityMismatch,
    OwnershipNotProven,
    ResourceConflict,
    ObservationChanged,
    VersionUnsupported,
    AuthenticationRequired,
    ResponseInvalid,
    ReadFailed,
    WriteRejected,
    WriteUncertain,
    VerificationFailed,
    JournalUnavailable,
    OperationInProgress,
    Cancelled,
    DeadlineExceeded,
}

public sealed record OperationResult
{
    private OperationResult(OperationOutcomeKind kind, OperationErrorCode code)
    {
        Kind = kind;
        Code = code;
    }

    public OperationOutcomeKind Kind { get; }

    public OperationErrorCode Code { get; }

    public bool IsSuccess => Kind is OperationOutcomeKind.Applied or OperationOutcomeKind.AlreadySatisfied;

    public static OperationResult Completed(bool applied) => new(
        applied ? OperationOutcomeKind.Applied : OperationOutcomeKind.AlreadySatisfied,
        OperationErrorCode.None
    );

    public static OperationResult Failed(OperationOutcomeKind kind, OperationErrorCode code)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind is OperationOutcomeKind.Applied or OperationOutcomeKind.AlreadySatisfied || code == OperationErrorCode.None)
            throw new ArgumentException("operation.failure.invalid");
        return new(kind, code);
    }
}

/// <summary>A read supplies either typed evidence or a safe failure, never a raw response.</summary>
public sealed class OperationRead<T> where T : class
{
    private OperationRead(T? value, OperationResult? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public OperationResult? Failure { get; }

    public static OperationRead<T> Observed(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static OperationRead<T> Failed(OperationResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.IsSuccess)
            throw new ArgumentException("operation.read.failure.invalid");
        return new(null, failure);
    }
}

/// <summary>An HTTP acknowledgement is not an Applied outcome; independent verification is mandatory.
/// A failure other than Unknown must prove that no mutation was sent or that it was rejected without
/// modification. An adapter cannot downgrade a possibly applied write to Transport or Cancelled.</summary>
public sealed class OperationWriteAttempt
{
    private OperationWriteAttempt(OperationResult? failure) => Failure = failure;

    public OperationResult? Failure { get; }

    public static OperationWriteAttempt Acknowledged() => new(null);

    public static OperationWriteAttempt Failed(OperationResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.IsSuccess)
            throw new ArgumentException("operation.write.failure.invalid");
        return new(failure);
    }
}
