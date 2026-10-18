using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class PinnedRuntimeProfileTests
{
    private static string Json => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "research-profile.json"));

    [Theory]
    [InlineData("image", "registry.example/qbittorrent:5.2.5")]
    [InlineData("runtimeVersion", "v5.2.5")]
    [InlineData("apiFamily", "Unreviewed API")]
    [InlineData("evidence", "Unverified")]
    public void RuntimeRejectsChangedServiceEvenWhenInventoryIsWellFormed(string field, string value)
    {
        var node = JsonNode.Parse(Json)!;
        node["services"]![0]![field] = value;
        var changed = ProfileReader.Parse(node.ToJsonString());
        var exception = Assert.Throws<InvalidDataException>(() => PinnedRuntimeProfile.EnsureCompatible(changed));
        Assert.Equal("profile.runtime.incompatible", exception.Message);
    }

    [Theory]
    [InlineData("source", "commit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("source", "tag", "v1.3.1")]
    [InlineData("artifacts", "sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("components", "commit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("components", "manifestVersion", "9.9.9")]
    [InlineData("coverage", "operation", "changed-operation")]
    public void RuntimeRejectsDriftOutsideTheSelectedService(string section, string field, string value)
    {
        var node = JsonNode.Parse(Json)!;
        var target = section == "source" ? node[section]! : node[section]![0]!;
        target[field] = value;
        var changed = ProfileReader.Parse(node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => PinnedRuntimeProfile.EnsureCompatible(changed));
    }

    [Fact]
    public void SnapshotDoesNotRetainExternalArraysOrExposeMutableMods()
    {
        var requested = ProfileReader.Parse(Json);
        PinnedRuntimeProfile.EnsureCompatible(requested);
        var fingerprint = PinnedRuntimeProfile.Fingerprint;
        var pinned = PinnedRuntimeProfile.Service("qbittorrent");
        var originalMod = Assert.Single(pinned.DockerMods);
        requested.Services[0].DockerMods[0] = "registry.example/mod:1.2.3";
        requested.Services[0] = requested.Services[0] with { Image = "registry.example/other:1.2.3" };

        var collection = Assert.IsAssignableFrom<IList<string>>(pinned.DockerMods);
        Assert.Throws<NotSupportedException>(() => collection[0] = "registry.example/mod:1.2.3");
        Assert.Equal(originalMod, Assert.Single(PinnedRuntimeProfile.Service("qbittorrent").DockerMods));
        Assert.Equal(fingerprint, PinnedRuntimeProfile.Fingerprint);
        Assert.Throws<InvalidDataException>(() => PinnedRuntimeProfile.EnsureCompatible(requested));
    }

    [Fact]
    public void InventoryV1RemainsReadableButCannotSelectRuntimeContracts()
    {
        var node = JsonNode.Parse(Json)!;
        node["schemaVersion"] = 1;
        foreach (var service in node["services"]!.AsArray())
            service!.AsObject().Remove("runtimeVersion");
        var legacy = ProfileReader.Parse(node.ToJsonString());
        Assert.Equal(1, legacy.SchemaVersion);
        Assert.Throws<InvalidDataException>(() => PinnedRuntimeProfile.EnsureCompatible(legacy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("v5.2.4\n")]
    [InlineData("v5.2.4?secret=synthetic")]
    public void SchemaTwoRejectsMissingOrInvalidRuntimeVersion(string? version)
    {
        var node = JsonNode.Parse(Json)!;
        node["services"]![0]!["runtimeVersion"] = version;
        Assert.Throws<InvalidDataException>(() => ProfileReader.Parse(node.ToJsonString()));
    }

    [Fact]
    public void InventoryV1DoesNotSilentlyAcceptSchemaTwoFields()
    {
        var node = JsonNode.Parse(Json)!;
        node["schemaVersion"] = 1;
        Assert.Throws<InvalidDataException>(() => ProfileReader.Parse(node.ToJsonString()));
    }

    [Fact]
    public void RuntimeSelectionRejectsMissingServiceAndUnprovenVersion()
    {
        Assert.Throws<InvalidDataException>(() => PinnedRuntimeProfile.Service("missing"));
        Assert.Throws<InvalidDataException>(() => PinnedRuntimeProfile.Version("dockerproxy"));
        Assert.Throws<InvalidDataException>(() => PinnedRuntimeProfile.Component("missing"));
    }

    [Fact]
    public void WorkflowUsesProfileSelectionAndPreservesUpstreamCheckoutIdentity()
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "profile-consumer-ci.yml"));
        var sourceRefs = Regex.Matches(workflow, @"repository: hhypest/hh-homelab\s+ref: ([^\s]+)");
        Assert.NotEmpty(sourceRefs);
        foreach (Match sourceRef in sourceRefs)
            Assert.Equal(PinnedRuntimeProfile.Source.Commit, sourceRef.Groups[1].Value);
        // The disposable SSH server has its own digest-pinned fixture, outside the install profile.
        var sshFixture = Regex.Match(workflow, @"(?ms)^  ssh-runtime:\r?\n.*?(?=^  [a-z][a-z0-9-]*:|\z)");
        Assert.True(sshFixture.Success);
        Assert.Contains("ssh-fixture/Dockerfile", sshFixture.Value);
        Assert.Contains("^alpine:[0-9.]+@sha256:[a-f0-9]{64}$", sshFixture.Value);
        Assert.Contains("bash .github/scripts/pull-image.sh \"$base_image\"", sshFixture.Value);
        Assert.DoesNotContain("bash .github/scripts/pull-image.sh ", workflow.Remove(sshFixture.Index, sshFixture.Length));
        Assert.Contains("bash .github/scripts/pull-profile-images.sh ", workflow);
    }

    [Fact]
    public void WhitespaceDoesNotChangeCompatibilityAndComponentLockStillMatches()
    {
        var compact = JsonNode.Parse(Json)!.ToJsonString();
        PinnedRuntimeProfile.EnsureCompatible(ProfileReader.Parse(compact));
        var component = PinnedRuntimeProfile.Component("monitor_docker");
        Assert.Equal(component.Commit, MonitorDockerPackage.Commit);
        Assert.Equal(component.ManifestVersion, MonitorDockerPackage.ManifestVersion);
        Assert.NotEmpty(MonitorDockerPackage.ReadLock());
    }
}
