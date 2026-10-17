using HhHomelab.Wizard.Domain;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class HomeAssistantRoleTests
{
    private static HomeAssistantNasSelection Nas =>
        new("nas-entry", "nas", "SYNTHETIC_NAS", "volume_1", "disk1", "disk2");
    private static Dictionary<HomeAssistantRole, string> Players =>
        new()
        {
            [HomeAssistantRole.LgPlayer] = "lg",
            [HomeAssistantRole.GxPlayer] = "gx",
            [HomeAssistantRole.GxRemote] = "remote",
            [HomeAssistantRole.GxCast] = "cast",
            [HomeAssistantRole.JellyfinPlayer] = "jellyfin",
        };

    private static HomeAssistantRolePlan Plan(HomeAssistantRoleInventory? inventory = null) =>
        HomeAssistantRolePlanner.Plan(inventory ?? Inventory(), Nas, Players);

    [Fact]
    public void TenRolesUseTechnicalKeysAndExplicitPlayers()
    {
        var plan = Plan();
        Assert.True(plan.CanApply);
        Assert.Equal(10, plan.Actions.Count);
        Assert.Equal(
            "sensor.ds725_volume_1_volume_used",
            plan.Actions.Single(a => a.Role == HomeAssistantRole.VolumeUsed).TargetId
        );
        Assert.Equal(
            "SYNTHETIC_NAS_SYNO.Storage.CGI.Storage:volume_percentage_used_volume_1",
            plan.Actions.Single(a => a.Role == HomeAssistantRole.VolumeUsed).Identity.UniqueId
        );
    }

    [Fact]
    public void PlayersAreNotSelectedAutomatically()
    {
        var plan = HomeAssistantRolePlanner.Plan(
            Inventory(),
            Nas,
            new Dictionary<HomeAssistantRole, string>()
        );
        Assert.False(plan.CanApply);
        Assert.Equal(5, plan.Problems.Count);
        Assert.All(plan.Problems, p => Assert.Equal("ha.roles.selection-required", p.Code));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("integration")]
    public void SelectedDisabledMetricPlansEnable(string reason)
    {
        var plan = Plan(
            ChangeEntity("cpu", e => e with { Identity = e.Identity with { DisabledBy = reason } })
        );
        Assert.True(plan.CanApply);
        Assert.True(plan.Actions.Single(a => a.Role == HomeAssistantRole.NasCpu).EnableRequired);
    }

    [Theory]
    [InlineData("config_entry")]
    [InlineData("device")]
    [InlineData("api-key")]
    [InlineData("hass")]
    public void ParentDisableReasonsBlockPlan(string reason)
    {
        var plan = Plan(
            ChangeEntity("cpu", e => e with { Identity = e.Identity with { DisabledBy = reason } })
        );
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Problems, p => p.Code == "ha.roles.enable-blocked");
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("metric")]
    [InlineData("entry")]
    [InlineData("device")]
    public void OtherNasOrWrongMetricIsNeverAdopted(string failure)
    {
        var plan = Plan(
            ChangeEntity(
                "volume",
                e =>
                    failure switch
                    {
                        "serial" => e with
                        {
                            Identity = e.Identity with
                            {
                                UniqueId = "OTHER_SYNO.API:volume_percentage_used_volume_1",
                            },
                        },
                        "metric" => e with
                        {
                            TranslationKey = "volume_size_used",
                            Identity = e.Identity with
                            {
                                UniqueId =
                                    "SYNTHETIC_NAS_SYNO.Storage.CGI.Storage:volume_size_used_volume_1",
                            },
                        },
                        "entry" => e with
                        {
                            Identity = e.Identity with { ConfigEntryId = "foreign" },
                        },
                        "device" => e with { Identity = e.Identity with { DeviceId = "disk1" } },
                        _ => e with
                        {
                            Identity = e.Identity with
                            {
                                UniqueId =
                                    "SYNTHETIC_NAS_FOREIGN.API:volume_percentage_used_volume_1",
                            },
                        },
                    }
            )
        );
        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.Problems,
            p => p.Role == HomeAssistantRole.VolumeUsed && p.Code == "ha.roles.entity-missing"
        );
    }

    [Fact]
    public void DuplicateTechnicalMetricRequiresResolution()
    {
        var inventory = Inventory();
        var cpu = inventory.Entities.Single(e => e.Identity.RegistryId == "cpu");
        var plan = Plan(
            inventory with
            {
                Entities =
                [
                    .. inventory.Entities,
                    cpu with
                    {
                        Identity = cpu.Identity with
                        {
                            RegistryId = "cpu2",
                            EntityId = "sensor.other_cpu",
                        },
                    },
                ],
            }
        );
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Problems, p => p.Code == "ha.roles.entity-ambiguous");
    }

    [Fact]
    public void OccupiedTargetIsNotOverwritten()
    {
        var inventory = Inventory();
        var other = inventory.Entities[0] with
        {
            Identity = inventory.Entities[0].Identity with
            {
                RegistryId = "foreign",
                EntityId = "media_player.lg_tv",
            },
        };
        var plan = Plan(inventory with { Entities = [.. inventory.Entities, other] });
        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.Problems,
            p => p.Role == HomeAssistantRole.LgPlayer && p.Code == "ha.roles.target-conflict"
        );
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unloaded")]
    [InlineData("domain")]
    public void IntegrationMustBeLoadedAndCorrect(string failure)
    {
        var inventory = Inventory();
        var plan = Plan(
            inventory with
            {
                Entries = inventory
                    .Entries.Select(e =>
                        e.Id == "nas-entry"
                            ? e with
                            {
                                DisabledBy = failure == "disabled" ? "user" : null,
                                State = failure == "unloaded" ? "setup_retry" : "loaded",
                                Domain = failure == "domain" ? "foreign" : e.Domain,
                            }
                            : e
                    )
                    .ToArray(),
            }
        );
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Problems, p => p.Code == "ha.roles.entry-not-loaded");
    }

    [Fact]
    public void GxPlayerAndRemoteMustBelongToSameDevice()
    {
        var plan = Plan(
            ChangeEntity(
                "remote",
                e => e with { Identity = e.Identity with { DeviceId = "gx-other" } }
            )
        );
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Problems, p => p.Code == "ha.roles.gx-device-mismatch");
    }

    [Fact]
    public void WrongParentVolumeDeviceBlocksMapping()
    {
        var inventory = Inventory();
        var plan = Plan(
            inventory with
            {
                Devices = inventory
                    .Devices.Select(d =>
                        d.Id == "volume_1" ? d with { ViaDeviceId = "other-nas" } : d
                    )
                    .ToArray(),
            }
        );
        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.Problems,
            p => p.Role == HomeAssistantRole.VolumeUsed && p.Code == "ha.roles.device-missing"
        );
    }

    [Fact]
    public void UnknownRegistrySelectionRemainsMissing()
    {
        var selected = Players;
        selected[HomeAssistantRole.LgPlayer] = "missing";
        Assert.Contains(
            HomeAssistantRolePlanner.Plan(Inventory(), Nas, selected).Problems,
            p => p.Role == HomeAssistantRole.LgPlayer && p.Code == "ha.roles.entity-missing"
        );
    }

    [Fact]
    public void DuplicateRegistrySnapshotIsRejected()
    {
        var inventory = Inventory();
        Assert.Throws<ArgumentException>(() =>
            Plan(inventory with { Entities = [.. inventory.Entities, inventory.Entities[0]] })
        );
    }

    [Fact]
    public void DriveSlotsMustHaveDistinctExplicitStorageIds() =>
        Assert.Throws<ArgumentException>(() =>
            HomeAssistantRolePlanner.Plan(
                Inventory(),
                Nas with
                {
                    Drive2Id = Nas.Drive1Id,
                },
                Players
            )
        );

    private static HomeAssistantRoleInventory ChangeEntity(
        string id,
        Func<HomeAssistantRoleEntity, HomeAssistantRoleEntity> change
    )
    {
        var inventory = Inventory();
        return inventory with
        {
            Entities = inventory
                .Entities.Select(e => e.Identity.RegistryId == id ? change(e) : e)
                .ToArray(),
        };
    }

    private static HomeAssistantRoleInventory Inventory()
    {
        HomeAssistantRoleEntity Metric(string id, string key, string device) =>
            new(
                new(
                    id,
                    "sensor.translated_" + id,
                    "SYNTHETIC_NAS_"
                        + (
                            key == "temperature" ? "SYNO.DSM.Info"
                            : key == "cpu_total_load" ? "SYNO.Core.System.Utilization"
                            : "SYNO.Storage.CGI.Storage"
                        )
                        + ":"
                        + key
                        + (device == "nas" ? "" : "_" + device),
                    "synology_dsm",
                    "nas-entry",
                    device,
                    null
                ),
                key,
                null
            );
        HomeAssistantRoleEntity Player(string id, string domain, string platform, string device) =>
            new(
                new(
                    id,
                    domain + ".translated_" + id,
                    "synthetic-" + id,
                    platform,
                    platform + "-entry",
                    device,
                    null
                ),
                null,
                null
            );
        var devices = new List<HomeAssistantRoleDevice>
        {
            new("nas", ["nas-entry"], [new("synology_dsm", "SYNTHETIC_NAS")], null, null),
        };
        foreach (var id in new[] { "volume_1", "disk1", "disk2" })
            devices.Add(
                new(id, ["nas-entry"], [new("synology_dsm", "SYNTHETIC_NAS_" + id)], "nas", null)
            );
        foreach (
            var (id, platform) in new[]
            {
                ("lg", "webostv"),
                ("gx", "androidtv_remote"),
                ("gx-other", "androidtv_remote"),
                ("cast", "cast"),
                ("jellyfin", "jellyfin"),
            }
        )
            devices.Add(
                new(id, [platform + "-entry"], [new(platform, "synthetic-" + id)], null, null)
            );
        return new(
            [
                Metric("temp", "temperature", "nas"),
                Metric("cpu", "cpu_total_load", "nas"),
                Metric("volume", "volume_percentage_used", "volume_1"),
                Metric("drive1", "disk_status", "disk1"),
                Metric("drive2", "disk_status", "disk2"),
                Player("lg", "media_player", "webostv", "lg"),
                Player("gx", "media_player", "androidtv_remote", "gx"),
                Player("remote", "remote", "androidtv_remote", "gx"),
                Player("cast", "media_player", "cast", "cast"),
                Player("jellyfin", "media_player", "jellyfin", "jellyfin"),
            ],
            devices,
            new[] { "synology_dsm", "webostv", "androidtv_remote", "cast", "jellyfin" }
                .Select(p => new HomeAssistantRoleEntry(
                    p == "synology_dsm" ? "nas-entry" : p + "-entry",
                    p,
                    "loaded",
                    null
                ))
                .ToArray()
        );
    }
}
