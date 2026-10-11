using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class HomeAssistantRuntimeTests
{
    [Fact]
    public void EmptySnapshotPreservesMissingHardwareAndPendingProxyObservation()
    {
        var result = Parse([]);
        Assert.False(result.MonitorDockerLoaded); Assert.Equal(40, result.DockerValues.Count); Assert.Equal(10, result.Hardware.Count);
        Assert.Equal(6, result.LocalScripts.Count);
        Assert.All(result.DockerValues.Concat(result.Hardware).Concat(result.LocalScripts), v => Assert.Equal(HomeAssistantValueStatus.Missing, v.Status));
        Assert.Null(result.DockerProxyError); Assert.Null(result.DockerOom); Assert.Null(result.DockerRestarting);
    }

    [Theory]
    [InlineData("unknown", HomeAssistantValueStatus.Unknown)]
    [InlineData("unavailable", HomeAssistantValueStatus.Unavailable)]
    [InlineData("0", HomeAssistantValueStatus.Available)]
    [InlineData("25.5", HomeAssistantValueStatus.Available)]
    [InlineData("NaN", HomeAssistantValueStatus.Invalid)]
    [InlineData("Infinity", HomeAssistantValueStatus.Invalid)]
    [InlineData("-1", HomeAssistantValueStatus.Invalid)]
    [InlineData("25,5", HomeAssistantValueStatus.Invalid)]
    [InlineData("not-a-number", HomeAssistantValueStatus.Invalid)]
    [InlineData("", HomeAssistantValueStatus.Invalid)]
    public void MetricsDistinguishUnknownUnavailableAndInvalidFromZero(string state, HomeAssistantValueStatus status)
    {
        var value = Parse([State("sensor.docker_radarr_cpu", state)]).DockerValues.Single(v => v.EntityId == "sensor.docker_radarr_cpu");
        Assert.Equal(status, value.Status);
        if (status != HomeAssistantValueStatus.Available) Assert.Null(value.Number);
    }

    [Theory]
    [InlineData("sensor.docker_down", "1.5")]
    [InlineData("sensor.docker_containers_total", "1e30")]
    [InlineData("sensor.docker_containers_running", "-1")]
    [InlineData("sensor.docker_radarr_memory_percent", "101")]
    [InlineData("sensor.docker_radarr_state", "secret-string")]
    [InlineData("script.tv_on", "unexpected")]
    public void ImpossibleOrUnrecognizedValuesAreInvalid(string id, string value)
    {
        var result = Parse([State(id, value)]);
        Assert.Equal(HomeAssistantValueStatus.Invalid, result.DockerValues.Concat(result.LocalScripts).Single(v => v.EntityId == id).Status);
    }

    [Theory]
    [InlineData("sensor.docker_radarr_state", "running")]
    [InlineData("sensor.docker_radarr_state", "exited")]
    [InlineData("script.tv_on", "off")]
    public void LegitimateInactiveStatesAreAvailableObservations(string id, string value)
    {
        var result = Parse([State(id, value)]);
        Assert.Equal(HomeAssistantValueStatus.Available, result.DockerValues.Concat(result.LocalScripts).Single(v => v.EntityId == id).Status);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    [InlineData("invalid")]
    public void StaleAttributesCannotReportHealthyProxy(string state)
    {
        var result = Parse([State("sensor.docker_down", state, new() { ["error"] = "", ["oom"] = false, ["restarting"] = 0 })]);
        Assert.Null(result.DockerProxyError); Assert.Null(result.DockerOom); Assert.Null(result.DockerRestarting);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("synthetic backend error", true)]
    public void ErrorAttributeIsReducedToFlagWithoutExportingText(string error, bool expected)
    {
        var result = Parse([State("sensor.docker_down", "0", new() { ["error"] = error, ["oom"] = true, ["restarting"] = 1 })]);
        Assert.Equal(expected, result.DockerProxyError); Assert.True(result.DockerOom); Assert.Equal(1, result.DockerRestarting);
        Assert.DoesNotContain("synthetic backend error", result.ToString());
    }

    [Fact]
    public void MissingOrMistypedAttributesRemainUnknown()
    {
        var result = Parse([State("sensor.docker_down", "0", new() { ["error"] = 0, ["oom"] = "false", ["restarting"] = -1 })]);
        Assert.Null(result.DockerProxyError); Assert.Null(result.DockerOom); Assert.Null(result.DockerRestarting);
    }

    [Fact]
    public void UnrelatedSensitiveStateAndAttributesAreNotExported()
    {
        var result = Parse([State("sensor.unrelated", "synthetic-sensitive-content", new() { ["token"] = "synthetic-token" })], true);
        Assert.True(result.MonitorDockerLoaded);
        Assert.DoesNotContain("synthetic-sensitive-content", result.ToString()); Assert.DoesNotContain("synthetic-token", result.ToString());
        Assert.DoesNotContain(result.DockerValues.Concat(result.Hardware).Concat(result.LocalScripts), v => v.EntityId == "sensor.unrelated");
    }

    [Fact]
    public void DuplicateEntitiesRejectAmbiguousSnapshot() => Assert.Equal("ha.runtime.snapshot-invalid",
        Assert.Throws<MediaContractException>(() => Parse([State("sensor.docker_down", "0"), State("sensor.docker_down", "1")])).Code);

    [Fact]
    public void InvalidEntityIdRejectsSnapshot() => Assert.Equal("ha.runtime.snapshot-invalid",
        Assert.Throws<MediaContractException>(() => Parse([State("not-an-entity", "0")])).Code);

    private static HomeAssistantRuntimeObservation Parse(JsonArray states, bool loaded = false) => HomeAssistantContractClient.ParseRuntimeStates(states, loaded);
    private static JsonObject State(string id, string value, JsonObject? attributes = null) => new()
    { ["entity_id"] = id, ["state"] = value, ["attributes"] = attributes ?? new JsonObject() };
}
