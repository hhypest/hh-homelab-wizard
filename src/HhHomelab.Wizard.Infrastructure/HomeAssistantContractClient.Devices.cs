using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

public sealed partial class HomeAssistantContractClient
{
    private sealed class DeviceIntent(HomeAssistantDeviceTarget target, string ownerId)
    {
        public HomeAssistantDeviceTarget Target { get; } = target;
        public string OwnerId { get; } = ownerId;
        public string? FlowId { get; set; }
        public string? EntryId { get; set; }
        public HomeAssistantDiscoveredFlow? Discovery { get; init; }
        public string Source => Discovery is null ? "user" : DiscoverySource(Discovery.Source);
        public string? StepId { get; set; }
        public string? DiscoveredHost { get; set; }
        public bool Unknown { get; set; } = true;
        public HomeAssistantDeviceProgress? Progress { get; set; }
    }

    private readonly Dictionary<HomeAssistantDeviceIntegration, DeviceIntent> deviceIntents = [];

    /// <summary>One USER flow per integration on an owned fresh HA. Caller serializes all operations.
    /// Memory-only intent: unknown writes cannot be retried, adopted by title or recovered after process exit.</summary>
    public async Task<HomeAssistantDeviceProgress> StartDeviceSetupAsync(
        HomeAssistantDeviceTarget target,
        CancellationToken token
    )
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var domain = DeviceDomain(target.Integration);
        if (target.Integration == HomeAssistantDeviceIntegration.GoogleCast)
        {
            if (target.Host is not null)
                throw new ArgumentException("ha.device.cast-target.invalid");
        }
        else
            ValidateDeviceHost(target.Host);
        _ = await CommandAsync("get_config", null, false, token);
        if (deviceIntents.TryGetValue(target.Integration, out var prior))
        {
            if (prior.Target != target || prior.OwnerId != OwnerId)
                throw new MediaContractException("ha.device.intent-conflict");
            return await ObserveDeviceSetupAsync(target.Integration, token);
        }
        if ((await DeviceEntriesAsync(domain, token)).Count != 0)
            throw new MediaContractException("ha.device.existing-entry");
        // This HA endpoint reports discovery/reauth flows, not other USER flows. It is not a lock.
        if (
            Array(await CommandAsync("config_entries/flow/progress", null, false, token))
                .Select(Object)
                .Any(f => Text(f, "handler") == domain)
        )
            throw new MediaContractException("ha.device.discovery-flow-present");
        var intent = new DeviceIntent(target, OwnerId!);
        deviceIntents.Add(target.Integration, intent); // Record before dispatch, also on cancellation.
        JsonObject result;
        try
        {
            result = Object(
                await HttpAsync(
                    HttpMethod.Post,
                    "api/config/config_entries/flow",
                    new() { ["handler"] = domain },
                    true,
                    "ha.device.start",
                    token
                )
            );
        }
        catch (MediaContractException e)
            when (e.Code == "ha.device.start.http-error"
                && e.StatusCode
                    is HttpStatusCode.BadRequest
                        or HttpStatusCode.Unauthorized
                        or HttpStatusCode.Forbidden
                        or HttpStatusCode.NotFound
            )
        {
            deviceIntents.Remove(target.Integration);
            throw;
        }
        intent.FlowId = DeviceIdentifier(result, "flow_id");
        return AcceptDeviceResult(result, intent, starting: true);
    }

    public Task<HomeAssistantDeviceProgress> SubmitDeviceHostAsync(
        HomeAssistantDeviceIntegration integration,
        CancellationToken token
    ) =>
        SubmitDeviceAsync(
            integration,
            HomeAssistantDeviceInput.Host,
            new() { ["host"] = RequireDeviceIntent(integration).Target.Host },
            token
        );

    /// <summary>HA verifies DSM TLS when SSL is selected; this contract has no certificate-bypass option.</summary>
    public Task<HomeAssistantDeviceProgress> SubmitDsmCredentialsAsync(
        string username,
        string password,
        int port,
        bool useSsl,
        CancellationToken token
    )
    {
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl))
            throw new ArgumentException("ha.device.credentials.invalid");
        if (string.IsNullOrEmpty(password) || password.Any(char.IsControl))
            throw new ArgumentException("ha.device.credentials.invalid");
        if (port is < 1 or > 65535)
            throw new ArgumentException("ha.device.port.invalid");
        var intent = RequireDeviceIntent(HomeAssistantDeviceIntegration.SynologyDsm);
        var payload = new JsonObject
        {
            ["username"] = username,
            ["password"] = password,
            ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ssl"] = useSsl,
            ["verify_ssl"] = true,
        };
        if (intent.Discovery is null)
            payload["host"] = intent.Target.Host;
        else if (intent.StepId == "user")
            payload["host"] = intent.DiscoveredHost;
        return SubmitDeviceAsync(
            HomeAssistantDeviceIntegration.SynologyDsm,
            HomeAssistantDeviceInput.DsmCredentials,
            payload,
            token
        );
    }

    public Task<HomeAssistantDeviceProgress> SubmitDsmOtpAsync(string otp, CancellationToken token)
    {
        if (otp is not { Length: 6 } || !otp.All(char.IsAsciiDigit))
            throw new ArgumentException("ha.device.otp.invalid");
        return SubmitDeviceAsync(
            HomeAssistantDeviceIntegration.SynologyDsm,
            HomeAssistantDeviceInput.DsmOtp,
            new() { ["otp_code"] = otp },
            token
        );
    }

    public Task<HomeAssistantDeviceProgress> SubmitDsmBackupLocationAsync(
        string share,
        string directory,
        CancellationToken token
    )
    {
        if (string.IsNullOrEmpty(directory) || directory.Length > 64)
            throw new ArgumentException("ha.device.backup-directory.invalid");
        if (!directory.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("ha.device.backup-directory.invalid");
        ValidateShare(share);
        return SubmitDeviceAsync(
            HomeAssistantDeviceIntegration.SynologyDsm,
            HomeAssistantDeviceInput.DsmBackupLocation,
            new() { ["backup_share"] = share, ["backup_path"] = directory },
            token
        );
    }

    /// <summary>Explicit action starts LG connection; user must accept the request on the TV.</summary>
    public Task<HomeAssistantDeviceProgress> ConfirmLgPairingAsync(CancellationToken token) =>
        SubmitDeviceAsync(
            HomeAssistantDeviceIntegration.LgWebOs,
            HomeAssistantDeviceInput.LgConfirmation,
            new(),
            token
        );

    public Task<HomeAssistantDeviceProgress> SubmitAndroidPinAsync(
        string pin,
        CancellationToken token
    )
    {
        if (string.IsNullOrEmpty(pin) || pin.Length > 64 || !pin.All(char.IsAsciiHexDigit))
            throw new ArgumentException("ha.device.pin.invalid");
        return SubmitDeviceAsync(
            HomeAssistantDeviceIntegration.AndroidTvRemote,
            HomeAssistantDeviceInput.AndroidPin,
            new() { ["pin"] = pin },
            token
        );
    }

    /// <summary>Known hosts are discovery hints, not a filter proving the GX1 Cast identity.</summary>
    public Task<HomeAssistantDeviceProgress> SubmitCastHostsAsync(
        IReadOnlyList<string> hosts,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var copy = hosts.ToArray();
        if (
            copy.Length > 32
            || copy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length
        )
            throw new ArgumentException("ha.device.cast-hosts.invalid");
        foreach (var host in copy)
            ValidateDeviceHost(host);
        return SubmitDeviceAsync(
            HomeAssistantDeviceIntegration.GoogleCast,
            HomeAssistantDeviceInput.CastHosts,
            new()
            {
                ["known_hosts"] = new JsonArray(
                    copy.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()
                ),
            },
            token
        );
    }

    public async Task<HomeAssistantDeviceProgress> ObserveDeviceSetupAsync(
        HomeAssistantDeviceIntegration integration,
        CancellationToken token
    )
    {
        var intent = RequireDeviceIntent(integration);
        if (intent.EntryId is { } id)
            return await ObserveDeviceEntryCoreAsync(integration, id, intent.Source, token);
        if (intent.Progress?.Stage == HomeAssistantDeviceSetupStage.Aborted)
            return intent.Progress;
        if (intent.FlowId is null || intent.Unknown)
            return UnknownDevice();
        if (intent.Discovery is not null)
            await VerifySelectedDiscoveryAsync(intent, token);
        // HA's GET reruns async_step_2sa(None), while pinned DSM requires a dict.
        // Keep the acknowledged OTP form; POST is the only supported continuation.
        if (intent.Progress?.Input == HomeAssistantDeviceInput.DsmOtp)
            return intent.Progress;
        try
        {
            var result = Object(
                await HttpAsync(
                    HttpMethod.Get,
                    "api/config/config_entries/flow/" + intent.FlowId,
                    null,
                    true,
                    "ha.device.observe",
                    token
                )
            );
            // GET can clear errors and does not prove whether a previous POST executed.
            var progress = ReadDeviceForm(result, intent);
            if (intent.Discovery is not null && Text(result, "step_id") != intent.StepId)
                throw new MediaContractException("ha.discovery.selection-conflict");
            if (progress.Input != intent.Progress?.Input)
                throw new MediaContractException("ha.device.flow-conflict");
            if (
                intent.Progress is { Problem: not HomeAssistantDeviceProblem.None } previous
                && progress.Input == previous.Input
                && progress.Problem == HomeAssistantDeviceProblem.None
            )
                progress = progress with { Problem = previous.Problem };
            intent.Progress = progress;
            return progress;
        }
        catch (MediaContractException e)
            when (e.Code == "ha.device.observe.http-error"
                && e.StatusCode == HttpStatusCode.NotFound
            )
        {
            intent.Unknown = true;
            return UnknownDevice();
        }
    }

    /// <summary>Read-only entry observation, also after restart. No adoption or proof of physical identity.</summary>
    public Task<HomeAssistantDeviceProgress> ObserveDeviceEntryAsync(
        HomeAssistantDeviceIntegration integration,
        string entryId,
        CancellationToken token
    ) => ObserveDeviceEntryCoreAsync(integration, entryId, "user", token);

    private async Task<HomeAssistantDeviceProgress> ObserveDeviceEntryCoreAsync(
        HomeAssistantDeviceIntegration integration,
        string entryId,
        string source,
        CancellationToken token
    )
    {
        DeviceIdentifierValue(entryId);
        var matches = (await DeviceEntriesAsync(DeviceDomain(integration), token))
            .Select(Object)
            .Where(e => Text(e, "entry_id") == entryId)
            .ToArray();
        if (matches.Length != 1)
            throw new MediaContractException("ha.device.entry-missing-or-conflict");
        var entry = matches[0];
        var entryConflicts =
            Text(entry, "domain") != DeviceDomain(integration)
            || Text(entry, "source") != source
            || entry["disabled_by"] is not null;
        if (entryConflicts)
            throw new MediaContractException("ha.device.entry-missing-or-conflict");
        return Text(entry, "state") switch
        {
            "loaded" => DeviceProgress(HomeAssistantDeviceSetupStage.EntryLoaded, entryId: entryId),
            "setup_in_progress" or "setup_retry" => DeviceProgress(
                HomeAssistantDeviceSetupStage.AwaitingSetup,
                entryId: entryId
            ),
            _ => throw new MediaContractException("ha.device.setup-failed"),
        };
    }

    private async Task<HomeAssistantDeviceProgress> SubmitDeviceAsync(
        HomeAssistantDeviceIntegration integration,
        HomeAssistantDeviceInput input,
        JsonObject payload,
        CancellationToken token
    )
    {
        var intent = RequireDeviceIntent(integration);
        var observed = await ObserveDeviceSetupAsync(integration, token);
        if (observed.Stage != HomeAssistantDeviceSetupStage.AwaitingInput)
            return observed;
        if (observed.Input != input)
            throw new MediaContractException("ha.device.wrong-step");
        if (
            input == HomeAssistantDeviceInput.DsmBackupLocation
            && !observed.BackupShares.Contains(
                Text(payload, "backup_share"),
                StringComparer.Ordinal
            )
        )
            throw new MediaContractException("ha.device.backup-share-conflict");
        // Recheck entries immediately before sending credentials or pairing confirmation.
        if ((await DeviceEntriesAsync(DeviceDomain(integration), token)).Count != 0)
            throw new MediaContractException("ha.device.existing-entry");
        intent.Unknown = true;
        var result = Object(
            await HttpAsync(
                HttpMethod.Post,
                "api/config/config_entries/flow/" + intent.FlowId,
                payload,
                true,
                "ha.device.submit",
                token
            )
        );
        var progress = AcceptDeviceResult(result, intent, starting: false);
        return intent.EntryId is not null
            ? await ObserveDeviceSetupAsync(integration, token)
            : progress;
    }

    private static HomeAssistantDeviceProgress AcceptDeviceResult(
        JsonObject result,
        DeviceIntent intent,
        bool starting
    )
    {
        CheckDeviceEnvelope(result, intent);
        var type = Text(result, "type");
        HomeAssistantDeviceProgress progress;
        if (type == "abort")
        {
            var problem = Text(result, "reason") switch
            {
                "already_configured" => HomeAssistantDeviceProblem.AlreadyConfigured,
                "already_in_progress" => HomeAssistantDeviceProblem.AlreadyInProgress,
                "cannot_connect" => HomeAssistantDeviceProblem.CannotConnect,
                _ => throw new MediaContractException("ha.device.abort-unsupported"),
            };
            progress = DeviceProgress(HomeAssistantDeviceSetupStage.Aborted, problem: problem);
        }
        else if (type == "create_entry" && !starting)
        {
            if (
                intent.Progress?.Input
                is not (
                    HomeAssistantDeviceInput.DsmCredentials
                    or HomeAssistantDeviceInput.DsmOtp
                    or HomeAssistantDeviceInput.DsmBackupLocation
                    or HomeAssistantDeviceInput.LgConfirmation
                    or HomeAssistantDeviceInput.AndroidPin
                    or HomeAssistantDeviceInput.CastHosts
                    or HomeAssistantDeviceInput.CastDiscoveryConfirmation
                )
            )
                throw new MediaContractException("ha.device.transition-unsupported");
            var entry = Object(result["result"]);
            if (
                Text(entry, "domain") != DeviceDomain(intent.Target.Integration)
                || Text(entry, "source") != intent.Source
            )
                throw new MediaContractException("ha.device.response-invalid");
            intent.EntryId = DeviceIdentifier(entry, "entry_id");
            progress = DeviceProgress(
                HomeAssistantDeviceSetupStage.AwaitingSetup,
                entryId: intent.EntryId
            );
        }
        else
        {
            progress = ReadDeviceForm(result, intent);
            var previous = intent.Progress?.Input;
            var allowed = starting
                ? progress.Input
                    == (
                        intent.Discovery is null
                            ? InitialDeviceInput(intent.Target.Integration)
                            : DiscoveryInput(intent.Discovery)
                    )
                : previous switch
                {
                    HomeAssistantDeviceInput.AndroidDiscoveryConfirmation => progress.Input
                        == HomeAssistantDeviceInput.AndroidPin,
                    HomeAssistantDeviceInput.Host => progress.Input
                        == (
                            intent.Target.Integration == HomeAssistantDeviceIntegration.LgWebOs
                                ? HomeAssistantDeviceInput.LgConfirmation
                                : HomeAssistantDeviceInput.AndroidPin
                        )
                        || progress.Input == HomeAssistantDeviceInput.Host,
                    HomeAssistantDeviceInput.DsmCredentials
                    or HomeAssistantDeviceInput.DsmOtp
                    or HomeAssistantDeviceInput.DsmBackupLocation => progress.Input
                        is HomeAssistantDeviceInput.DsmCredentials
                            or HomeAssistantDeviceInput.DsmOtp
                            or HomeAssistantDeviceInput.DsmBackupLocation,
                    _ => progress.Input == previous,
                };
            if (!allowed)
                throw new MediaContractException("ha.device.transition-unsupported");
        }
        intent.Unknown = false;
        intent.Progress = progress;
        if (type == "form")
            intent.StepId = Text(result, "step_id");
        return progress;
    }

    private static HomeAssistantDeviceProgress ReadDeviceForm(
        JsonObject result,
        DeviceIntent intent
    )
    {
        CheckDeviceEnvelope(result, intent);
        if (Text(result, "type") != "form")
            throw new MediaContractException("ha.device.interaction-unsupported");
        var (input, fields) = (intent.Target.Integration, Text(result, "step_id")) switch
        {
            (HomeAssistantDeviceIntegration.SynologyDsm, "user") => (
                HomeAssistantDeviceInput.DsmCredentials,
                new[] { "host", "username", "password", "port", "ssl", "verify_ssl" }
            ),
            (HomeAssistantDeviceIntegration.SynologyDsm, "link")
                when intent.Discovery is not null => (
                HomeAssistantDeviceInput.DsmCredentials,
                ["username", "password", "port", "ssl", "verify_ssl"]
            ),
            (HomeAssistantDeviceIntegration.SynologyDsm, "2sa") => (
                HomeAssistantDeviceInput.DsmOtp,
                ["otp_code"]
            ),
            (HomeAssistantDeviceIntegration.SynologyDsm, "backup_share") => (
                HomeAssistantDeviceInput.DsmBackupLocation,
                ["backup_share", "backup_path"]
            ),
            (
                HomeAssistantDeviceIntegration.LgWebOs
                    or HomeAssistantDeviceIntegration.AndroidTvRemote,
                "user"
            ) => (HomeAssistantDeviceInput.Host, ["host"]),
            (HomeAssistantDeviceIntegration.LgWebOs, "pairing") => (
                HomeAssistantDeviceInput.LgConfirmation,
                []
            ),
            (HomeAssistantDeviceIntegration.AndroidTvRemote, "pair") => (
                HomeAssistantDeviceInput.AndroidPin,
                ["pin"]
            ),
            (HomeAssistantDeviceIntegration.AndroidTvRemote, "zeroconf_confirm")
                when intent.Discovery is not null => (
                HomeAssistantDeviceInput.AndroidDiscoveryConfirmation,
                []
            ),
            (HomeAssistantDeviceIntegration.GoogleCast, "config") => (
                HomeAssistantDeviceInput.CastHosts,
                ["known_hosts"]
            ),
            (HomeAssistantDeviceIntegration.GoogleCast, "confirm")
                when intent.Discovery is not null => (
                HomeAssistantDeviceInput.CastDiscoveryConfirmation,
                []
            ),
            _ => throw new MediaContractException("ha.device.interaction-unsupported"),
        };
        var schema = Array(result["data_schema"]).Select(Object).ToArray();
        if (
            !schema
                .Select(f => Text(f, "name"))
                .Order(StringComparer.Ordinal)
                .SequenceEqual(fields.Order(StringComparer.Ordinal))
        )
            throw new MediaContractException("ha.device.schema-unsupported");
        if (intent.Discovery is not null && input == HomeAssistantDeviceInput.DsmCredentials)
        {
            var host =
                Text(result, "step_id") == "link"
                    ? Text(Object(result["description_placeholders"]), "host")
                    : Text(schema.Single(f => Text(f, "name") == "host"), "default");
            ValidateDeviceHost(host);
            if (intent.DiscoveredHost is not null && intent.DiscoveredHost != host)
                throw new MediaContractException("ha.discovery.host-conflict");
            intent.DiscoveredHost = host;
        }
        IReadOnlyList<string> shares = System.Array.Empty<string>();
        if (input == HomeAssistantDeviceInput.DsmBackupLocation)
        {
            var selector = Object(
                Object(schema.Single(f => Text(f, "name") == "backup_share")["selector"])["select"]
            );
            var values = Array(selector["options"])
                .Select(Object)
                .Select(o => Text(o, "value"))
                .ToArray();
            if (
                values.Length is < 1 or > 256
                || values.Distinct(StringComparer.Ordinal).Count() != values.Length
            )
                throw new MediaContractException("ha.device.backup-selector-invalid");
            foreach (var value in values)
                ValidateShare(value);
            shares = System.Array.AsReadOnly(values);
        }
        var errors = result["errors"] is null ? new JsonObject() : Object(result["errors"]);
        if (errors.Count > 1)
            throw new MediaContractException("ha.device.flow-rejected");
        var problem = HomeAssistantDeviceProblem.None;
        if (errors.Count == 1)
        {
            var error = errors.Single();
            problem = (input, error.Key, error.Value?.GetValue<string>()) switch
            {
                (HomeAssistantDeviceInput.DsmCredentials, "username", "invalid_auth")
                or (HomeAssistantDeviceInput.AndroidPin, "base", "invalid_auth") =>
                    HomeAssistantDeviceProblem.InvalidAuth,
                (HomeAssistantDeviceInput.DsmCredentials, "host", "cannot_connect")
                or (
                    HomeAssistantDeviceInput.Host
                        or HomeAssistantDeviceInput.LgConfirmation,
                    "base",
                    "cannot_connect"
                ) => HomeAssistantDeviceProblem.CannotConnect,
                (HomeAssistantDeviceInput.DsmCredentials, "base", "missing_data") =>
                    HomeAssistantDeviceProblem.MissingData,
                (HomeAssistantDeviceInput.DsmOtp, "otp_code", "otp_failed") =>
                    HomeAssistantDeviceProblem.OtpRejected,
                (HomeAssistantDeviceInput.LgConfirmation, "base", "error_pairing") =>
                    HomeAssistantDeviceProblem.PairingRejected,
                _ => throw new MediaContractException("ha.device.flow-rejected"),
            };
        }
        return new(HomeAssistantDeviceSetupStage.AwaitingInput, input, problem, null, shares);
    }

    private DeviceIntent RequireDeviceIntent(HomeAssistantDeviceIntegration integration)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!deviceIntents.TryGetValue(integration, out var intent) || intent.OwnerId != OwnerId)
            throw new MediaContractException("ha.device.intent-required");
        return intent;
    }

    private async Task<JsonArray> DeviceEntriesAsync(string domain, CancellationToken token) =>
        Array(
            await HttpAsync(
                HttpMethod.Get,
                "api/config/config_entries/entry?domain=" + domain,
                null,
                true,
                "ha.device.entries",
                token
            )
        );

    private static void CheckDeviceEnvelope(JsonObject result, DeviceIntent intent)
    {
        if (
            Text(result, "handler") != DeviceDomain(intent.Target.Integration)
            || DeviceIdentifier(result, "flow_id") != intent.FlowId
        )
            throw new MediaContractException("ha.device.flow-conflict");
    }

    private static string DeviceIdentifier(JsonObject result, string key)
    {
        var id = Text(result, key);
        DeviceIdentifierValue(id);
        return id;
    }

    private static void DeviceIdentifierValue(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(char.IsAsciiLetterOrDigit))
            throw new MediaContractException("ha.device.identifier-invalid");
    }

    private static void ValidateDeviceHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253)
            throw new ArgumentException("ha.device.host.invalid");
        var hasForbiddenChars =
            host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || host.Any(c => c is '/' or '\\' or '@' or '?' or '#' or '%' or '[' or ']');
        if (hasForbiddenChars || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("ha.device.host.invalid");
    }

    private static void ValidateShare(string share)
    {
        if (string.IsNullOrEmpty(share) || share.Length > 1024)
            throw new MediaContractException("ha.device.backup-share-invalid");
        var shapeInvalid =
            !share.StartsWith('/') || share.Contains('\\') || share.Any(char.IsControl);
        if (shapeInvalid)
            throw new MediaContractException("ha.device.backup-share-invalid");
        var segmentsInvalid = share.Split('/').Skip(1).Any(p => p is "" or "." or "..");
        if (segmentsInvalid)
            throw new MediaContractException("ha.device.backup-share-invalid");
    }

    private static string DeviceDomain(HomeAssistantDeviceIntegration integration) =>
        integration switch
        {
            HomeAssistantDeviceIntegration.SynologyDsm => "synology_dsm",
            HomeAssistantDeviceIntegration.LgWebOs => "webostv",
            HomeAssistantDeviceIntegration.AndroidTvRemote => "androidtv_remote",
            HomeAssistantDeviceIntegration.GoogleCast => "cast",
            _ => throw new ArgumentException("ha.device.integration.invalid"),
        };

    private static HomeAssistantDeviceInput InitialDeviceInput(
        HomeAssistantDeviceIntegration integration
    ) =>
        integration switch
        {
            HomeAssistantDeviceIntegration.SynologyDsm => HomeAssistantDeviceInput.DsmCredentials,
            HomeAssistantDeviceIntegration.GoogleCast => HomeAssistantDeviceInput.CastHosts,
            _ => HomeAssistantDeviceInput.Host,
        };

    private static HomeAssistantDeviceProgress UnknownDevice() =>
        DeviceProgress(HomeAssistantDeviceSetupStage.OutcomeUnknown);

    private static HomeAssistantDeviceProgress DeviceProgress(
        HomeAssistantDeviceSetupStage stage,
        HomeAssistantDeviceProblem problem = HomeAssistantDeviceProblem.None,
        string? entryId = null
    ) => new(stage, HomeAssistantDeviceInput.None, problem, entryId, System.Array.Empty<string>());
}
