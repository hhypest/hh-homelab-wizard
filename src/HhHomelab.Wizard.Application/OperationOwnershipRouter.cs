using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

/// <summary>One ownership port for a graph: each fresh check goes to the operation that owns exactly that
/// resource identity. A resource no operation owns is Foreign; nothing is cached.</summary>
public sealed class OperationOwnershipRouter : IOperationOwnership
{
    private readonly Dictionary<ResourceIdentity, IOperationOwnership> owners = [];

    public OperationOwnershipRouter(IEnumerable<IConfigurationOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var operation in operations)
        {
            if (operation is not IOperationOwnership ownership || operation.Descriptor is not { IsValid: true } descriptor)
                throw new ArgumentException("ownership.router.operation");
            if (!owners.TryAdd(descriptor.Identity.Resource, ownership))
                throw new ArgumentException("ownership.router.duplicate");
        }
    }

    public Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token)
    {
        if (resource is null || !owners.TryGetValue(resource, out var owner))
            return Task.FromResult(ResourceOwnership.Foreign);
        return owner.CheckAsync(resource, token);
    }
}
