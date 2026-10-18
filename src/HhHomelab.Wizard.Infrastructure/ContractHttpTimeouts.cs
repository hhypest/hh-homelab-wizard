namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Finite budgets for one HTTP exchange, including response buffering and consumption.
/// A smaller HttpClient.Timeout is respected without changing the caller's client.</summary>
public sealed class ContractHttpTimeouts
{
    public static ContractHttpTimeouts Default { get; } =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

    internal static ContractHttpTimeouts HomeAssistant { get; } =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    internal static ContractHttpTimeouts Revocation { get; } =
        new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    public ContractHttpTimeouts(TimeSpan read, TimeSpan write)
    {
        Validate(read, nameof(read));
        Validate(write, nameof(write));
        Read = read;
        Write = write;
    }

    public TimeSpan Read { get; }

    public TimeSpan Write { get; }

    internal TimeSpan Budget(bool isRead, TimeSpan clientTimeout)
    {
        var configured = isRead ? Read : Write;
        if (clientTimeout == Timeout.InfiniteTimeSpan)
            return configured;
        return clientTimeout < configured ? clientTimeout : configured;
    }

    private static void Validate(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(name);
    }
}
