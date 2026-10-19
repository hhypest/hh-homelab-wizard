using System.Security.Cryptography;
using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Executable graph of the first qBittorrent increment: the ownership marker, then Compose and
/// .env rendered from the plan. Built only from a fresh plan without blocking checks; unresolved
/// Unverified/NotChecked checks stay on the plan and are not an authorization. Operation and resource IDs
/// derive from the installation ID, so a new process rebuilds the same graph and the journal resumes it.
/// Directories of mounts, the network and the container are later steps.</summary>
public sealed class QbittorrentInstallationGraph : IDisposable
{
    public static TimeSpan StepTimeout { get; } = TimeSpan.FromMinutes(2);

    private readonly ConfigurationFileRequest[] files;

    private QbittorrentInstallationGraph(ConfigurationOperationPlan plan, IReadOnlyList<IConfigurationOperation> operations,
        ConfigurationFileRequest[] files)
    {
        Plan = plan;
        Operations = operations;
        Ownership = new OperationOwnershipRouter(operations);
        this.files = files;
    }

    public ConfigurationOperationPlan Plan { get; }

    public IReadOnlyList<IConfigurationOperation> Operations { get; }

    public IOperationOwnership Ownership { get; }

    public static QbittorrentInstallationGraph Create(QbittorrentInstallationPlan plan, SshHostKey pin, DateTimeOffset now,
        INasPathProbe paths, NasOwnershipMarkerWriter markerWriter, NasConfigurationFileClient fileClient)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(markerWriter);
        ArgumentNullException.ThrowIfNull(fileClient);
        if (plan.HasBlockingChecks)
            throw new ArgumentException("graph.plan.blocking");
        if (now < plan.ObservedAt || now - plan.ObservedAt > QbittorrentInstallationPlan.MaximumEvidenceAge)
            throw new ArgumentException("graph.plan.stale");
        if (!pin.IsValid || pin.EndpointFingerprint != plan.Request.Target.EndpointFingerprint)
            throw new ArgumentException("graph.pin.mismatch");
        var marker = new OwnershipMarkerRequest(Identity(plan, "marker"), plan.Request.Layout, plan.Owner);
        var markerStep = new ConfigurationPlanStep(marker.Descriptor, [], StepTimeout);
        var operations = new List<IConfigurationOperation> { new NasOwnershipMarkerOperation(marker, paths, markerWriter, pin) };
        var steps = new List<ConfigurationPlanStep> { markerStep };
        var requests = new List<ConfigurationFileRequest>();
        try
        {
            foreach (var file in QbittorrentConfigurationRenderer.Render(plan))
            {
                var request = new ConfigurationFileRequest(Identity(plan, "file:" + file.RelativePath), plan.Request.Layout,
                    plan.Owner, file.RelativePath, file.Mode, file.Content);
                requests.Add(request);
                operations.Add(new NasConfigurationFileOperation(request, fileClient, pin));
                steps.Add(new ConfigurationPlanStep(request.Descriptor, [markerStep.Descriptor.Identity.OperationId], StepTimeout));
            }
            return new(new ConfigurationOperationPlan(steps), operations.AsReadOnly(), requests.ToArray());
        }
        catch
        {
            foreach (var request in requests)
                request.Dispose();
            throw;
        }
    }

    /// <summary>Zeroes the rendered file contents held by the file requests.</summary>
    public void Dispose()
    {
        foreach (var request in files)
            request.Dispose();
    }

    private static OperationIdentity Identity(QbittorrentInstallationPlan plan, string role)
    {
        var installation = plan.Owner.InstallationId;
        var resource = new ResourceIdentity(installation, StableId(installation, "resource:" + role), "nas",
            plan.Owner.ProfileFingerprint, plan.Request.Target.EndpointFingerprint);
        return new(StableId(installation, "operation:" + role), resource);
    }

    /// <summary>Name-based GUID (RFC 9562 version 8) from the installation and a fixed role label.</summary>
    internal static Guid StableId(Guid installation, string role)
    {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(role)];
        installation.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(role, input.AsSpan(16));
        var bytes = SHA256.HashData(input)[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
