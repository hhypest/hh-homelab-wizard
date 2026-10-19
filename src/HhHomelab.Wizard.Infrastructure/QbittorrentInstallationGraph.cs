using System.Security.Cryptography;
using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Executable graph of the first qBittorrent increment: the ownership marker, then the mount
/// directories of /config and /watch and the Compose and .env files rendered from the plan. Built only from a
/// fresh plan without blocking checks; unresolved Unverified/NotChecked checks stay on the plan and are not an
/// authorization. Operation and resource IDs derive from the installation ID, so a new process rebuilds the
/// same graph and the journal resumes it. The network and the container are later steps.</summary>
public sealed class QbittorrentInstallationGraph : IDisposable
{
    public static TimeSpan StepTimeout { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Octal 750: the container user owns its mounts, the DSM group may read, others may not.</summary>
    public const int MountDirectoryMode = 0x1E8;

    private readonly ConfigurationFileRequest[] files;
    private readonly ConfigurationOperationPlan plan;
    private readonly IReadOnlyList<IConfigurationOperation> operations;
    private readonly IOperationOwnership ownership;
    private bool disposed;

    private QbittorrentInstallationGraph(ConfigurationOperationPlan plan, IReadOnlyList<IConfigurationOperation> operations,
        ConfigurationFileRequest[] files)
    {
        this.plan = plan;
        this.operations = operations;
        ownership = new OperationOwnershipRouter(operations);
        this.files = files;
    }

    public ConfigurationOperationPlan Plan => Live(plan);

    public IReadOnlyList<IConfigurationOperation> Operations => Live(operations);

    public IOperationOwnership Ownership => Live(ownership);

    public static QbittorrentInstallationGraph Create(QbittorrentInstallationPlan plan, SshHostKey pin, DateTimeOffset now,
        INasPathProbe paths, NasOwnershipMarkerWriter markerWriter, NasConfigurationDirectoryClient directoryClient,
        NasConfigurationFileClient fileClient)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(markerWriter);
        ArgumentNullException.ThrowIfNull(directoryClient);
        ArgumentNullException.ThrowIfNull(fileClient);
        if (plan.HasBlockingChecks)
            throw new ArgumentException("graph.plan.blocking");
        // Measured from the oldest evidence, not from when the plan object was built.
        if (now < plan.ObservedAt || now - plan.EvidenceStartedAt > QbittorrentInstallationPlan.MaximumEvidenceAge)
            throw new ArgumentException("graph.plan.stale");
        // Writes use exactly the host key the plan's evidence was collected with.
        if (pin != plan.HostKey)
            throw new ArgumentException("graph.pin.mismatch");
        var marker = new OwnershipMarkerRequest(Identity(plan, "marker"), plan.Request.Layout, plan.Owner);
        var markerStep = new ConfigurationPlanStep(marker.Descriptor, [], StepTimeout);
        var operations = new List<IConfigurationOperation> { new NasOwnershipMarkerOperation(marker, paths, markerWriter, pin) };
        var steps = new List<ConfigurationPlanStep> { markerStep };
        // Created before the container starts: otherwise Docker creates the mounts itself, owned by root.
        foreach (var relative in new[] { plan.Profile.ConfigurationPath, plan.Profile.WatchPath })
        {
            var directory = new ConfigurationDirectoryRequest(Identity(plan, "directory:" + relative), plan.Request.Layout,
                plan.Owner, relative, MountDirectoryMode);
            operations.Add(new NasConfigurationDirectoryOperation(directory, directoryClient, pin));
            steps.Add(new ConfigurationPlanStep(directory.Descriptor, [markerStep.Descriptor.Identity.OperationId], StepTimeout));
        }
        var requests = new List<ConfigurationFileRequest>();
        var rendered = QbittorrentConfigurationRenderer.Render(plan);
        try
        {
            foreach (var file in rendered)
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
        finally
        {
            // The requests hold their own copies; the rendered buffers are not kept.
            foreach (var file in rendered)
                file.Dispose();
        }
    }

    /// <summary>Zeroes the rendered file contents held by the file requests; the graph is unusable afterwards.</summary>
    public void Dispose()
    {
        disposed = true;
        foreach (var request in files)
            request.Dispose();
    }

    private T Live<T>(T value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return value;
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
