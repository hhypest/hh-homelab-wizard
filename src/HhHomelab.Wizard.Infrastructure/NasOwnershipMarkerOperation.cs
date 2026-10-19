using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Creating the ownership marker as a journaled operation. Observe and Verify are fresh read-only
/// path inspections; ownership is proven only by an absent/empty (or staged) root or by this exact
/// marker. Any other contents, a changed profile or an unusable root is a conflict, never adopted.
/// The write re-checks preconditions and publishes with a no-clobber link, so an interrupted attempt
/// (staged or not started) may be completed under the same pending intent.</summary>
public sealed class NasOwnershipMarkerOperation : IConfigurationOperation, IOperationOwnership, IRepeatableConditionalApply
{
    private readonly OwnershipMarkerRequest request;
    private readonly INasPathProbe probe;
    private readonly NasOwnershipMarkerWriter writer;
    private readonly SshHostKey pin;

    public NasOwnershipMarkerOperation(OwnershipMarkerRequest request, INasPathProbe probe, NasOwnershipMarkerWriter writer, SshHostKey pin)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(pin);
        if (!request.IsValid())
            throw new ArgumentException("ownership.request.invalid");
        // The journaled resource must name the NAS that is actually written: the pinned SSH endpoint.
        if (request.Identity.Resource.EndpointFingerprint != pin.EndpointFingerprint)
            throw new ArgumentException("ownership.endpoint.mismatch");
        this.request = request;
        this.probe = probe;
        this.writer = writer;
        this.pin = pin;
    }

    public OperationDescriptor Descriptor => request.Descriptor;

    public async Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token)
    {
        var read = await ReadAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return OperationRead<OperationObservation>.Failed(read.Failure);
        var (state, revision) = read.Value!;
        return OperationRead<OperationObservation>.Observed(new(Descriptor, state, revision));
    }

    public async Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token)
    {
        if (!baseline.IsValidFor(Descriptor) || baseline.State != ObservedResourceState.Missing)
            return OperationWriteAttempt.Failed(OperationResult.Failed(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest));
        var outcome = await writer.WriteAsync(request, pin, token).ConfigureAwait(false);
        return outcome switch
        {
            MarkerWriteOutcome.Created or MarkerWriteOutcome.AlreadyPresent => OperationWriteAttempt.Acknowledged(),
            MarkerWriteOutcome.Rejected => Write(OperationOutcomeKind.Rejected, OperationErrorCode.WriteRejected),
            // The write connection presented another host key than the pin: same handling as a read.
            MarkerWriteOutcome.IdentityChanged => Write(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch),
            // The connection failed before the command left the client: nothing was sent.
            MarkerWriteOutcome.NotSent when token.IsCancellationRequested => Write(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled),
            MarkerWriteOutcome.NotSent => Write(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed),
            _ => Write(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain),
        };
    }

    public async Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token)
    {
        var read = await ReadAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return OperationRead<OperationVerification>.Failed(read.Failure);
        var (state, revision) = read.Value!;
        return OperationRead<OperationVerification>.Observed(new(Descriptor, state, revision));
    }

    public async Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token)
    {
        if (resource != Descriptor.Identity.Resource)
            return ResourceOwnership.Foreign;
        var read = await ReadAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return ResourceOwnership.Unproven;
        return read.Value!.State == ObservedResourceState.Conflict ? ResourceOwnership.Foreign : ResourceOwnership.Owned;
    }

    /// <summary>Missing: new installation (absent, empty or staged root). Satisfied: this exact marker.
    /// Conflict: anything else, including a configuration root path that does not pass its own check.</summary>
    private async Task<OperationRead<StateRead>> ReadAsync(CancellationToken token)
    {
        var result = await probe.InspectAsync(request.Layout, pin, token).ConfigureAwait(false);
        if (result.Status != SshProbeStatus.Ready || result.Snapshot is not { } snapshot)
            return OperationRead<StateRead>.Failed(ReadFailure(result.Status));
        var resource = request.Identity.Resource;
        var sameTarget = snapshot.HostKey == pin && snapshot.HostKey.EndpointFingerprint == resource.EndpointFingerprint;
        var sameProfile = snapshot.ProfileFingerprint == resource.ProfileFingerprint;
        var sameRoot = snapshot.Layout.ConfigurationRoot == request.Layout.ConfigurationRoot;
        if (!sameTarget || !sameProfile || !sameRoot)
            return OperationRead<StateRead>.Failed(OperationResult.Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch));
        var checks = RemotePathAssessment.Evaluate(snapshot, request.Owner);
        var rootPassed = checks.Any(check => check.Code == "paths.configuration-root" && check.Status == PreflightCheckStatus.Passed);
        var ownership = InstallationOwnershipAssessment.Evaluate(snapshot, request.Owner);
        var state = ownership.Code switch
        {
            _ when !rootPassed => ObservedResourceState.Conflict,
            "ownership.own-installation" => ObservedResourceState.Satisfied,
            "ownership.new-installation" => ObservedResourceState.Missing,
            _ => ObservedResourceState.Conflict,
        };
        var revision = ConfigurationRequest.Fingerprint(new { snapshot.Ownership.State, snapshot.Ownership.Marker });
        return OperationRead<StateRead>.Observed(new(state, revision));
    }

    private static OperationResult ReadFailure(SshProbeStatus status) => status switch
    {
        SshProbeStatus.AuthenticationRequired => OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.AuthenticationRequired),
        SshProbeStatus.Cancelled => OperationResult.Failed(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled),
        SshProbeStatus.InvalidResponse => OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ResponseInvalid),
        SshProbeStatus.HostKeyApprovalRequired or SshProbeStatus.HostKeyChanged
            => OperationResult.Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch),
        _ => OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed),
    };

    private static OperationWriteAttempt Write(OperationOutcomeKind kind, OperationErrorCode code) =>
        OperationWriteAttempt.Failed(OperationResult.Failed(kind, code));

    private sealed record StateRead(ObservedResourceState State, string Revision);
}
