using System.Text.Json;
using System.Text.Json.Serialization;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

internal sealed record OperationJournalData(
    int SchemaVersion,
    OperationJournalScope Scope,
    long Generation,
    OperationJournalEntry[] Entries,
    OperationPlanBinding? Plan
);

internal sealed record OperationJournalSnapshot(OperationJournalData Data, string Hash)
{
    internal const int MaximumBytes = 4 * 1024 * 1024;

    internal const int MaximumEntries = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
    };

    internal static OperationJournalSnapshot Create(OperationJournalScope scope, long generation, OperationJournalEntry[] entries, OperationPlanBinding? plan = null)
    {
        var data = new OperationJournalData(2, scope, generation, entries, plan);
        return new OperationJournalSnapshot(data, ConfigurationRequest.Fingerprint(data));
    }

    internal byte[] Encode()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (bytes.Length > MaximumBytes)
            throw new OperationJournalException("journal.capacity");
        return bytes;
    }

    internal static OperationJournalSnapshot Decode(byte[] bytes, OperationJournalScope expected)
    {
        var snapshot = JsonSerializer.Deserialize<OperationJournalSnapshot>(bytes, JsonOptions);
        if (snapshot?.Data is not { } data || data.Scope != expected)
            throw new OperationJournalException("journal.identity");
        if (data.SchemaVersion != 2)
            throw new OperationJournalException("journal.schema");
        if (data.Generation < 0 || data.Entries is null)
            throw new OperationJournalException("journal.invalid");
        if (snapshot.Hash != ConfigurationRequest.Fingerprint(data))
            throw new OperationJournalException("journal.hash");
        data.Plan?.Validate(data.Scope);
        ValidateEntries(data);
        return snapshot;
    }

    private static void ValidateEntries(OperationJournalData data)
    {
        if (data.Entries.Length > MaximumEntries)
            throw new OperationJournalException("journal.capacity");
        var operationIds = new HashSet<Guid>();
        var resourceIds = new Dictionary<Guid, ResourceIdentity>();
        var pendingResources = new HashSet<Guid>();
        foreach (var entry in data.Entries)
        {
            if (entry?.Intent is not { } intent || intent.Descriptor is not { IsValid: true } descriptor)
                throw new OperationJournalException("journal.invalid");
            ValidateIntent(intent, data.Scope);
            data.Plan?.RequireMember(descriptor);
            if (entry.Confirmation is not null && !entry.Confirmation.IsSatisfiedFor(descriptor))
                throw new OperationJournalException("journal.invalid");
            if (!operationIds.Add(descriptor.Identity.OperationId))
                throw new OperationJournalException("journal.duplicate");
            var resource = descriptor.Identity.Resource;
            if (resourceIds.TryGetValue(resource.ResourceId, out var previous) && previous != resource)
                throw new OperationJournalException("journal.identity");
            resourceIds[resource.ResourceId] = resource;
            if (entry.Confirmation is null && !pendingResources.Add(resource.ResourceId))
                throw new OperationJournalException("journal.duplicate");
        }
    }

    internal static void ValidateIntent(OperationIntent intent, OperationJournalScope scope)
    {
        ValidateDescriptor(intent.Descriptor, scope);
        if (intent.Baseline is not { State: ObservedResourceState.Missing } baseline)
            throw new OperationJournalException("journal.baseline");
        if (!baseline.IsValidFor(intent.Descriptor))
            throw new OperationJournalException("journal.baseline");
    }

    internal static void ValidateDescriptor(OperationDescriptor descriptor, OperationJournalScope scope)
    {
        if (descriptor is not { IsValid: true })
            throw new OperationJournalException("journal.descriptor");
        var resource = descriptor.Identity.Resource;
        var installationChanged = resource.InstallationId != scope.InstallationId;
        var profileChanged = resource.ProfileFingerprint != scope.ProfileFingerprint;
        if (installationChanged || profileChanged)
            throw new OperationJournalException("journal.identity");
    }
}
