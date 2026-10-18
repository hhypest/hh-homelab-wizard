namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Classifies a failed HTTP exchange, not an entire installation operation.
/// Contract does not prove that a mutation was rejected or that retry is safe.</summary>
public enum ContractFailureKind
{
    Contract,
    Transport,
    Unknown,
}
