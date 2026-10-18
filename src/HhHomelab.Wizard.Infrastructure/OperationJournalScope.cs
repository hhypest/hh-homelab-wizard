using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Local session identity supplied from trusted installation metadata. Does not prove NAS ownership.</summary>
public sealed record OperationJournalScope(Guid InstallationId, Guid SessionId, string ProfileFingerprint)
{
    public bool IsValid => InstallationId != Guid.Empty
        && SessionId != Guid.Empty
        && ResourceIdentity.IsFingerprint(ProfileFingerprint);
}

/// <summary>Safe storage failure code, without paths, input JSON or underlying exception text.</summary>
public sealed class OperationJournalException(string code) : IOException(code)
{
    public string Code { get; } = code;
}
