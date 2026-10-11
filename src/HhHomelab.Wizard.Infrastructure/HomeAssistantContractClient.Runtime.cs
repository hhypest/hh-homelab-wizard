using System.Globalization;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using static HhHomelab.Wizard.Infrastructure.MediaJson;

namespace HhHomelab.Wizard.Infrastructure;

public sealed partial class HomeAssistantContractClient
{
    private static readonly string[] Containers = ["qbittorrent", "prowlarr", "radarr", "sonarr", "flaresolverr", "jellyfin", "seerr", "homeassistant", "dockerproxy"];
    private static readonly string[] HardwareIds = ["sensor.ds725_temperature", "sensor.ds725_cpu_utilization_total", "sensor.ds725_volume_1_volume_used",
        "sensor.ds725_drive_1_status", "sensor.ds725_drive_2_status", "media_player.lg_tv", "media_player.rocktek_gx1", "media_player.rocktek_gx1_cast",
        "media_player.jellyfin_rocktek_gx1", "remote.rocktek_gx1"];

    /// <summary>Read-only, restricted observation of the pinned local profile. Does not invent readiness.</summary>
    public async Task<HomeAssistantRuntimeObservation> ReadLocalProfileRuntimeAsync(CancellationToken token)
    {
        var config = Object(await CommandAsync("get_config", null, false, token));
        var loaded = Array(config["components"]).Any(c => c?.GetValue<string>() == "monitor_docker");
        return ParseRuntimeStates(Array(await CommandAsync("get_states", null, false, token)), loaded);
    }

    internal static HomeAssistantRuntimeObservation ParseRuntimeStates(JsonArray states, bool monitorLoaded)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in states)
        {
            var state = Object(node); var id = Text(state, "entity_id");
            if (!HomeAssistantDashboard.IsEntityId(id) || !map.TryAdd(id, state))
                throw new MediaContractException("ha.runtime.snapshot-invalid");
        }
        HomeAssistantValueObservation Observe(string id, bool numeric = false)
        {
            if (!map.TryGetValue(id, out var node)) return new(id, HomeAssistantValueStatus.Missing);
            var value = Text(node, "state");
            var status = value switch { "unknown" => HomeAssistantValueStatus.Unknown, "unavailable" => HomeAssistantValueStatus.Unavailable,
                _ => HomeAssistantValueStatus.Available };
            if (status != HomeAssistantValueStatus.Available) return new(id, status);
            if (!numeric)
            {
                if (value.Length == 0 || (id.StartsWith("sensor.docker_", StringComparison.Ordinal) && id.EndsWith("_state", StringComparison.Ordinal) &&
                    value is not ("running" or "paused" or "restarting" or "exited" or "created" or "dead" or "removing")) ||
                    (id.StartsWith("script.", StringComparison.Ordinal) && value is not ("on" or "off")))
                    return new(id, HomeAssistantValueStatus.Invalid);
                return new(id, status);
            }
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 ||
                ((id is "sensor.docker_down" or "sensor.docker_containers_running" or "sensor.docker_containers_total") && (number != Math.Truncate(number) || number > int.MaxValue)) ||
                (id.EndsWith("_memory_percent", StringComparison.Ordinal) && number > 100))
                return new(id, HomeAssistantValueStatus.Invalid);
            return new(id, status, number);
        }
        var docker = new List<HomeAssistantValueObservation>
        {
            Observe("sensor.docker_version"), Observe("sensor.docker_containers_running", true),
            Observe("sensor.docker_containers_total", true), Observe("sensor.docker_down", true)
        };
        foreach (var container in Containers)
        {
            docker.Add(Observe("sensor.docker_" + container + "_state"));
            foreach (var metric in new[] { "cpu", "memory", "memory_percent" }) docker.Add(Observe("sensor.docker_" + container + "_" + metric, true));
        }
        bool? error = null, oom = null; int? restarting = null;
        if (Observe("sensor.docker_down", true).Status == HomeAssistantValueStatus.Available &&
            map.TryGetValue("sensor.docker_down", out var down) && down["attributes"] is JsonObject attributes)
        {
            if (attributes["error"] is JsonValue e && e.TryGetValue<string>(out var text)) error = text.Length != 0;
            if (attributes["oom"] is JsonValue o && o.TryGetValue<bool>(out var flag)) oom = flag;
            if (attributes["restarting"] is JsonValue r && r.TryGetValue<int>(out var count) && count >= 0) restarting = count;
        }
        return new(monitorLoaded, docker, HardwareIds.Select(id => Observe(id)).ToArray(),
            new[] { "script.tv_on", "script.tv_off", "script.tv_youtube", "script.tv_jellyfin", "script.tv_jellyfin_resume", "script.hh_homelab_notify" }.Select(id => Observe(id)).ToArray(), error, oom, restarting);
    }

    // Fixture-only mutations: caller must supply an owned local bench. No arbitrary command endpoint.
    internal Task RefreshDockerDownProbeAsync(CancellationToken token) => CommandAsync("call_service", new()
    { ["domain"] = "homeassistant", ["service"] = "update_entity", ["service_data"] = new JsonObject { ["entity_id"] = "sensor.docker_down" } }, true, token);

    internal Task NotifyLocalProfileProbeAsync(CancellationToken token) => CommandAsync("call_service", new()
    { ["domain"] = "script", ["service"] = "hh_homelab_notify", ["service_data"] = new JsonObject { ["title"] = "Wizard M0 fixture", ["text"] = "Synthetic local notification" } }, true, token);

    internal async Task<bool> HasLocalProfileProbeNotificationAsync(CancellationToken token) =>
        Array(await CommandAsync("persistent_notification/get", null, false, token)).Any(n => n?["title"]?.GetValue<string>() == "Wizard M0 fixture");
}
