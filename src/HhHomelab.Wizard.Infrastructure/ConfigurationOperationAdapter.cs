using System.Net;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Sample API binding. Requires the scenario's ownership/journal lease; API auth alone
/// does not establish NAS ownership. No persistent operation state or automatic retry.</summary>
public abstract class ConfigurationOperationAdapter : IConfigurationOperation
{
    protected ConfigurationOperationAdapter(ConfigurationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    protected ConfigurationRequest Request { get; }

    public OperationDescriptor Descriptor => Request.Descriptor;

    protected abstract bool MatchesService { get; }

    protected abstract string EndpointFingerprint { get; }

    protected abstract Task<(ObservedResourceState State, string Revision)> ReadAsync(CancellationToken token);

    protected abstract Task WriteAsync(CancellationToken token);

    public async Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token)
    {
        if (!Request.IsValid() || !MatchesService)
            return FailedRead(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest);
        if (Request.Identity.Resource.ProfileFingerprint != PinnedRuntimeProfile.Fingerprint)
            return FailedRead(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch);
        if (Request.Identity.Resource.EndpointFingerprint != EndpointFingerprint)
            return FailedRead(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch);
        try
        {
            var observed = await ReadAsync(token).ConfigureAwait(false);
            return OperationRead<OperationObservation>.Observed(new(Descriptor, observed.State, observed.Revision));
        }
        catch (ContractException exception)
        {
            return OperationRead<OperationObservation>.Failed(MapReadFailure(exception));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return FailedRead(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled);
        }
        catch (Exception)
        {
            return FailedRead(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed);
        }
    }

    public async Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token)
    {
        if (!baseline.IsValidFor(Descriptor) || baseline.State != ObservedResourceState.Missing)
            return OperationWriteAttempt.Failed(OperationResult.Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch));
        var read = await ObserveAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return OperationWriteAttempt.Failed(read.Failure);
        if (read.Value != baseline)
            return OperationWriteAttempt.Failed(OperationResult.Failed(OperationOutcomeKind.Conflict, OperationErrorCode.ObservationChanged));
        if (token.IsCancellationRequested)
            return OperationWriteAttempt.Failed(OperationResult.Failed(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled));
        try
        {
            await WriteAsync(token).ConfigureAwait(false);
            return OperationWriteAttempt.Acknowledged();
        }
        catch (Exception)
        {
            // No status code in these sample POST contracts proves absence of a modification.
            return OperationWriteAttempt.Failed(OperationResult.Failed(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain));
        }
    }

    public async Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token)
    {
        var read = await ObserveAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return OperationRead<OperationVerification>.Failed(read.Failure);
        var observed = read.Value!;
        return OperationRead<OperationVerification>.Observed(new(observed.Descriptor, observed.State, observed.Revision));
    }

    private static OperationResult MapReadFailure(ContractException exception)
    {
        if (exception.Kind == ContractFailureKind.Transport)
            return OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed);
        var authenticationDenied = exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        var authenticationRead = exception.Code is "qb.version.unavailable" or "arr.read.http-error";
        if (authenticationDenied && authenticationRead)
            return OperationResult.Failed(OperationOutcomeKind.Invalid, OperationErrorCode.AuthenticationRequired);
        return exception.Code switch
        {
            "qb.version.unsupported" or "arr.version.unsupported" => OperationResult.Failed(OperationOutcomeKind.Invalid, OperationErrorCode.VersionUnsupported),
            "qb.session.required" => OperationResult.Failed(OperationOutcomeKind.Invalid, OperationErrorCode.AuthenticationRequired),
            "arr.root.conflict" or "arr.root.inaccessible" => OperationResult.Failed(OperationOutcomeKind.Conflict, OperationErrorCode.ResourceConflict),
            _ => OperationResult.Failed(OperationOutcomeKind.Invalid, OperationErrorCode.ResponseInvalid),
        };
    }

    private static OperationRead<OperationObservation> FailedRead(OperationOutcomeKind kind, OperationErrorCode code) =>
        OperationRead<OperationObservation>.Failed(OperationResult.Failed(kind, code));
}
