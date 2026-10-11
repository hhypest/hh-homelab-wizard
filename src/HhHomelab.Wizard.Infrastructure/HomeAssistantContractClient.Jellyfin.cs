using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

public sealed partial class HomeAssistantContractClient
{
    private sealed class JellyfinIntent(Uri url, string serverId, string username, string ownerId)
    {
        public Uri Url { get; } = url;
        public string ServerId { get; } = serverId;
        public string Username { get; } = username;
        public string OwnerId { get; } = ownerId;
        public string? FlowId { get; set; }
        public string? EntryId { get; set; }
        public bool SubmissionUnknown { get; set; }
    }
    private JellyfinIntent? jellyfinIntent;

    /// <summary>Starts one owned USER flow on a fresh HA. Caller serializes operations.
    /// Intent is memory-only in M0; an unknown start cannot be retried or rediscovered.</summary>
    public async Task<HomeAssistantJellyfinProgress> StartJellyfinSetupAsync(Uri url, string serverId, string username, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArrContractClient.ValidateEndpoint(url); Credentials(username, "validation-only"); ValidateServerId(serverId);
        // Verify the current authenticated owner before associating an in-memory intent.
        _ = await CommandAsync("get_config", null, false, token);
        if (jellyfinIntent is { } prior)
        {
            if (prior.Url != url || prior.ServerId != serverId || prior.Username != username || prior.OwnerId != OwnerId)
                throw new MediaContractException("ha.jellyfin.intent-conflict");
            return await ObserveJellyfinSetupAsync(token);
        }
        if ((await JellyfinEntriesAsync(token)).Count != 0) throw new MediaContractException("ha.jellyfin.existing-entry");
        var intent = new JellyfinIntent(url, serverId, username, OwnerId!);
        jellyfinIntent = intent; // Before the write, including caller cancellation or response loss.
        JsonObject result;
        try { result = Object(await HttpAsync(HttpMethod.Post, "api/config/config_entries/flow", new() { ["handler"] = "jellyfin" }, true, "ha.jellyfin.start", token)); }
        catch (MediaContractException e) when (e.Code == "ha.jellyfin.start.http-error" &&
            e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        { jellyfinIntent = null; throw; }
        ValidateUserForm(result);
        intent.FlowId = Identifier(result, "flow_id");
        return new(HomeAssistantJellyfinStage.AwaitingCredentials, null);
    }

    /// <summary>Explicit credentials submission. A definitive rejected form permits corrected input.
    /// A lost successful creation response never causes automatic resubmission or entry adoption.</summary>
    public async Task<HomeAssistantJellyfinProgress> SubmitJellyfinCredentialsAsync(string password, CancellationToken token)
    {
        var intent = RequireJellyfinIntent(); Credentials(intent.Username, password);
        var observed = await ObserveJellyfinSetupAsync(token);
        if (observed.Stage is not (HomeAssistantJellyfinStage.AwaitingCredentials or HomeAssistantJellyfinStage.AuthenticationRejected or HomeAssistantJellyfinStage.CannotConnect))
            return observed;
        intent.SubmissionUnknown = true;
        var result = Object(await HttpAsync(HttpMethod.Post, "api/config/config_entries/flow/" + intent.FlowId,
            new() { ["url"] = intent.Url.AbsoluteUri.TrimEnd('/'), ["username"] = intent.Username, ["password"] = password }, true, "ha.jellyfin.submit", token));
        if (Text(result, "type") == "form") return ObserveJellyfinForm(result, intent);
        if (Text(result, "type") != "create_entry" || Text(result, "handler") != "jellyfin" || Identifier(result, "flow_id") != intent.FlowId)
            throw new MediaContractException("ha.jellyfin.response-invalid");
        var entry = Object(result["result"]);
        if (Text(entry, "domain") != "jellyfin" || Text(entry, "source") != "user") throw new MediaContractException("ha.jellyfin.response-invalid");
        intent.EntryId = Identifier(entry, "entry_id");
        intent.SubmissionUnknown = false;
        return await ObserveJellyfinSetupAsync(token);
    }

    public async Task<HomeAssistantJellyfinProgress> ObserveJellyfinSetupAsync(CancellationToken token)
    {
        var intent = RequireJellyfinIntent();
        if (intent.EntryId is { } id) return await ObserveJellyfinEntryAsync(id, intent.ServerId, token);
        if (intent.FlowId is null) return new(HomeAssistantJellyfinStage.OutcomeUnknown, null);
        try
        {
            var result = Object(await HttpAsync(HttpMethod.Get, "api/config/config_entries/flow/" + intent.FlowId, null, true, "ha.jellyfin.observe", token));
            return ObserveJellyfinForm(result, intent);
        }
        catch (MediaContractException e) when (e.Code == "ha.jellyfin.observe.http-error" && e.StatusCode == HttpStatusCode.NotFound)
        { return new(HomeAssistantJellyfinStage.OutcomeUnknown, null); }
    }

    /// <summary>Read-only confirmation of a caller-supplied entry/server identity, also after restart.
    /// This does not adopt an entry, establish account identity, or authorize further writes.</summary>
    public async Task<HomeAssistantJellyfinProgress> ObserveJellyfinEntryAsync(string entryId, string serverId, CancellationToken token)
    {
        ValidateIdentifier(entryId); ValidateServerId(serverId);
        var matches = (await JellyfinEntriesAsync(token)).Select(Object).Where(e => Text(e, "entry_id") == entryId).ToArray();
        if (matches.Length != 1) throw new MediaContractException("ha.jellyfin.entry-missing-or-conflict");
        var entry = matches[0];
        if (Text(entry, "domain") != "jellyfin" || entry["disabled_by"] is not null)
            throw new MediaContractException("ha.jellyfin.entry-disabled-or-conflict");
        var state = Text(entry, "state");
        if (state is "setup_in_progress" or "setup_retry") return new(HomeAssistantJellyfinStage.AwaitingSetup, entryId);
        if (state != "loaded") throw new MediaContractException("ha.jellyfin.setup-failed");
        var devices = Array(await CommandAsync("config/device_registry/list", null, false, token)).Select(Object)
            .Where(d => Array(d["config_entries"]).Any(e => e?.GetValue<string>() == entryId)).ToArray();
        if (!devices.Any(d => Array(d["identifiers"]).Any(n => n is JsonArray a && a.Count == 2 &&
            a[0]?.GetValue<string>() == "jellyfin" && a[1]?.GetValue<string>() == serverId) && Text(d, "sw_version") == "12.2.0"))
            throw new MediaContractException("ha.jellyfin.server-identity-mismatch");
        return new(HomeAssistantJellyfinStage.Loaded, entryId);
    }

    private async Task<JsonArray> JellyfinEntriesAsync(CancellationToken token) =>
        Array(await HttpAsync(HttpMethod.Get, "api/config/config_entries/entry?domain=jellyfin", null, true, "ha.jellyfin.entries", token));
    private JellyfinIntent RequireJellyfinIntent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (jellyfinIntent is not { } intent || intent.OwnerId != OwnerId) throw new MediaContractException("ha.jellyfin.intent-required");
        return intent;
    }
    private static HomeAssistantJellyfinProgress ObserveJellyfinForm(JsonObject result, JellyfinIntent intent)
    {
        ValidateUserForm(result);
        if (Identifier(result, "flow_id") != intent.FlowId) throw new MediaContractException("ha.jellyfin.flow-conflict");
        var errors = Object(result["errors"]);
        if (errors.Count == 0) return new(intent.SubmissionUnknown ? HomeAssistantJellyfinStage.OutcomeUnknown : HomeAssistantJellyfinStage.AwaitingCredentials, null);
        var stage = errors["base"]?.GetValue<string>() switch
        {
            "invalid_auth" => HomeAssistantJellyfinStage.AuthenticationRejected,
            "cannot_connect" => HomeAssistantJellyfinStage.CannotConnect,
            _ => throw new MediaContractException("ha.jellyfin.flow-rejected")
        };
        intent.SubmissionUnknown = false;
        return new(stage, null);
    }
    private static void ValidateUserForm(JsonObject result)
    {
        if (Text(result, "type") != "form" || Text(result, "handler") != "jellyfin" || Text(result, "step_id") != "user")
            throw new MediaContractException("ha.jellyfin.interaction-unsupported");
        var fields = Array(result["data_schema"]).Select(Object).Select(f => Text(f, "name")).Order(StringComparer.Ordinal).ToArray();
        if (!fields.SequenceEqual(new[] { "password", "url", "username" })) throw new MediaContractException("ha.jellyfin.schema-unsupported");
    }
    private static string Identifier(JsonObject result, string key) { var id = Text(result, key); ValidateIdentifier(id); return id; }
    private static void ValidateIdentifier(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("ha.jellyfin.identifier.invalid");
    }
    private static void ValidateServerId(string id)
    {
        if (id is not { Length: 32 } || !id.All(char.IsAsciiHexDigit)) throw new ArgumentException("ha.jellyfin.server-id.invalid");
    }
}
