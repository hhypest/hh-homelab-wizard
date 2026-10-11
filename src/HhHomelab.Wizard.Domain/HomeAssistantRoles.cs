namespace HhHomelab.Wizard.Domain;

public enum HomeAssistantRole { NasTemperature, NasCpu, VolumeUsed, Drive1Status, Drive2Status, LgPlayer, GxPlayer, GxRemote, GxCast, JellyfinPlayer }
public enum HomeAssistantEnableStage { Unchanged, ReloadScheduled, RestartRequired }
public sealed record HomeAssistantRoleEntity(HomeAssistantEntity Identity, string? TranslationKey, string? HiddenBy);
public sealed record HomeAssistantDeviceIdentifier(string Domain, string Value);
public sealed record HomeAssistantRoleDevice(string Id, IReadOnlyList<string> ConfigEntries,
    IReadOnlyList<HomeAssistantDeviceIdentifier> Identifiers, string? ViaDeviceId, string? DisabledBy);
public sealed record HomeAssistantRoleEntry(string Id, string Domain, string State, string? DisabledBy);
public sealed record HomeAssistantRoleInventory(IReadOnlyList<HomeAssistantRoleEntity> Entities,
    IReadOnlyList<HomeAssistantRoleDevice> Devices, IReadOnlyList<HomeAssistantRoleEntry> Entries);
public sealed record HomeAssistantNasSelection(string EntryId, string DeviceId, string Serial, string VolumeId, string Drive1Id, string Drive2Id);
public sealed record HomeAssistantRoleAction(HomeAssistantRole Role, HomeAssistantEntity Identity, string TargetId, bool EnableRequired);
public sealed record HomeAssistantRoleProblem(HomeAssistantRole Role, string Code);
public sealed record HomeAssistantRolePlan(IReadOnlyList<HomeAssistantRoleAction> Actions, IReadOnlyList<HomeAssistantRoleProblem> Problems)
{
    public bool CanApply => Problems.Count == 0 && Actions.Count == 10;
}

/// <summary>Pure planning from technical identities. No first-player or localized-name fallback.</summary>
public static class HomeAssistantRolePlanner
{
    private static readonly (HomeAssistantRole Role, string Target, string Platform, string? Metric)[] Roles =
    [
        (HomeAssistantRole.NasTemperature, "sensor.ds725_temperature", "synology_dsm", "temperature"),
        (HomeAssistantRole.NasCpu, "sensor.ds725_cpu_utilization_total", "synology_dsm", "cpu_total_load"),
        (HomeAssistantRole.VolumeUsed, "sensor.ds725_volume_1_volume_used", "synology_dsm", "volume_percentage_used"),
        (HomeAssistantRole.Drive1Status, "sensor.ds725_drive_1_status", "synology_dsm", "disk_status"),
        (HomeAssistantRole.Drive2Status, "sensor.ds725_drive_2_status", "synology_dsm", "disk_status"),
        (HomeAssistantRole.LgPlayer, "media_player.lg_tv", "webostv", null),
        (HomeAssistantRole.GxPlayer, "media_player.rocktek_gx1", "androidtv_remote", null),
        (HomeAssistantRole.GxRemote, "remote.rocktek_gx1", "androidtv_remote", null),
        (HomeAssistantRole.GxCast, "media_player.rocktek_gx1_cast", "cast", null),
        (HomeAssistantRole.JellyfinPlayer, "media_player.jellyfin_rocktek_gx1", "jellyfin", null)
    ];

    public static HomeAssistantRolePlan Plan(HomeAssistantRoleInventory inventory, HomeAssistantNasSelection nas,
        IReadOnlyDictionary<HomeAssistantRole, string> playerRegistryIds)
    {
        ArgumentNullException.ThrowIfNull(inventory); ArgumentNullException.ThrowIfNull(nas); ArgumentNullException.ThrowIfNull(playerRegistryIds);
        if (new[] { nas.EntryId, nas.DeviceId, nas.Serial, nas.VolumeId, nas.Drive1Id, nas.Drive2Id }.Any(string.IsNullOrWhiteSpace) ||
            nas.Drive1Id == nas.Drive2Id || nas.Serial.Contains(':') ||
            playerRegistryIds.Keys.Any(r => !Roles.Any(x => x.Role == r && x.Metric is null)))
            throw new ArgumentException("ha.roles.selection-invalid");
        if (inventory.Entities.Select(e => e.Identity.RegistryId).Distinct().Count() != inventory.Entities.Count ||
            inventory.Entities.Select(e => e.Identity.EntityId).Distinct().Count() != inventory.Entities.Count ||
            inventory.Devices.Select(d => d.Id).Distinct().Count() != inventory.Devices.Count ||
            inventory.Entries.Select(e => e.Id).Distinct().Count() != inventory.Entries.Count)
            throw new ArgumentException("ha.roles.snapshot-invalid");
        var actions = new List<HomeAssistantRoleAction>(); var problems = new List<HomeAssistantRoleProblem>();
        void Problem(HomeAssistantRole role, string code) => problems.Add(new(role, "ha.roles." + code));
        var root = inventory.Devices.SingleOrDefault(d => d.Id == nas.DeviceId);
        bool RootValid() => root is not null && root.DisabledBy is null && root.ConfigEntries.Contains(nas.EntryId) &&
            root.Identifiers.Contains(new("synology_dsm", nas.Serial));
        foreach (var spec in Roles)
        {
            HomeAssistantRoleEntity[] candidates;
            if (spec.Metric is { } metric)
            {
                if (!RootValid()) { Problem(spec.Role, "nas-device-invalid"); continue; }
                var storage = spec.Role switch { HomeAssistantRole.VolumeUsed => nas.VolumeId, HomeAssistantRole.Drive1Status => nas.Drive1Id,
                    HomeAssistantRole.Drive2Status => nas.Drive2Id, _ => null };
                var identifier = nas.Serial + (storage is null ? "" : "_" + storage);
                var devices = inventory.Devices.Where(d => d.ConfigEntries.Contains(nas.EntryId) &&
                    d.Identifiers.Contains(new("synology_dsm", identifier)) && (storage is null ? d.Id == nas.DeviceId : d.ViaDeviceId == nas.DeviceId)).ToArray();
                if (devices.Length != 1) { Problem(spec.Role, devices.Length == 0 ? "device-missing" : "device-ambiguous"); continue; }
                if (devices[0].DisabledBy is not null) { Problem(spec.Role, "device-disabled"); continue; }
                var suffix = ":" + metric + (storage is null ? "" : "_" + storage);
                var apiKey = metric switch { "temperature" => "SYNO.DSM.Info", "cpu_total_load" => "SYNO.Core.System.Utilization", _ => "SYNO.Storage.CGI.Storage" };
                var uniqueId = nas.Serial + "_" + apiKey + suffix;
                candidates = inventory.Entities.Where(e => e.Identity.Platform == spec.Platform && e.Identity.ConfigEntryId == nas.EntryId &&
                    e.Identity.DeviceId == devices[0].Id && e.TranslationKey == metric &&
                    e.Identity.UniqueId == uniqueId).ToArray();
            }
            else
            {
                if (!playerRegistryIds.TryGetValue(spec.Role, out var selected)) { Problem(spec.Role, "selection-required"); continue; }
                candidates = inventory.Entities.Where(e => e.Identity.RegistryId == selected).ToArray();
            }
            if (candidates.Length != 1) { Problem(spec.Role, candidates.Length == 0 ? "entity-missing" : "entity-ambiguous"); continue; }
            var entity = candidates[0].Identity;
            if (!HomeAssistantDashboard.IsEntityId(entity.EntityId) || entity.EntityId.Split('.')[0] != spec.Target.Split('.')[0] ||
                entity.Platform != spec.Platform || string.IsNullOrWhiteSpace(entity.UniqueId) ||
                string.IsNullOrWhiteSpace(entity.ConfigEntryId) || string.IsNullOrWhiteSpace(entity.DeviceId))
            { Problem(spec.Role, "identity-invalid"); continue; }
            var entry = inventory.Entries.SingleOrDefault(e => e.Id == entity.ConfigEntryId);
            var device = inventory.Devices.SingleOrDefault(d => d.Id == entity.DeviceId);
            if (entry is null || entry.Domain != spec.Platform || entry.State != "loaded" || entry.DisabledBy is not null)
            { Problem(spec.Role, "entry-not-loaded"); continue; }
            if (device is null || device.DisabledBy is not null || !device.ConfigEntries.Contains(entry.Id))
            { Problem(spec.Role, "device-invalid"); continue; }
            if (entity.DisabledBy is not (null or "integration" or "user")) { Problem(spec.Role, "enable-blocked"); continue; }
            if (inventory.Entities.Any(e => e.Identity.EntityId == spec.Target && e.Identity.RegistryId != entity.RegistryId))
            { Problem(spec.Role, "target-conflict"); continue; }
            actions.Add(new(spec.Role, entity, spec.Target, entity.DisabledBy is not null));
        }
        var gx = actions.SingleOrDefault(a => a.Role == HomeAssistantRole.GxPlayer);
        var remote = actions.SingleOrDefault(a => a.Role == HomeAssistantRole.GxRemote);
        if (gx is not null && remote is not null && (gx.Identity.DeviceId != remote.Identity.DeviceId || gx.Identity.ConfigEntryId != remote.Identity.ConfigEntryId))
            Problem(HomeAssistantRole.GxRemote, "gx-device-mismatch");
        if (actions.Select(a => a.Identity.RegistryId).Distinct().Count() != actions.Count)
            throw new ArgumentException("ha.roles.duplicate-selection");
        return new(actions.AsReadOnly(), problems.AsReadOnly());
    }
}
