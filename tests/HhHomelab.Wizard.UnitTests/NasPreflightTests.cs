using System.Text;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class NasPreflightTests
{
    [Fact]
    public void CompleteInventoryIsObservedButDoesNotProveVersionCompatibilityOrInstallationReadiness()
    {
        var facts = NasPreflightProtocol.Parse(0, PreflightData.Output);
        var snapshot = PreflightData.Snapshot(facts);
        var checks = NasPreflightAssessment.Evaluate(snapshot);
        Assert.Equal("7.4.1-90080", facts.Dsm.Version);
        Assert.Equal("24.0.2-1535", facts.ContainerManager.Version);
        Assert.Empty(facts.ContainerIds);
        Assert.Contains(checks, check => check.Code == "preflight.containers.empty" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "preflight.dsm" && check.Status == PreflightCheckStatus.Unverified);
        Assert.Contains(checks, check => check.Code == "preflight.paths-acl" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Contains(checks, check => check.Code == "preflight.ownership" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.False(checks.All(check => check.Status == PreflightCheckStatus.Passed));
    }

    [Fact]
    public void MissingSoftwareAndUnreadableSoftwareRemainDifferentFacts()
    {
        var output = PreflightData.Output.Replace("dsm=available:7.4.1-90080", "dsm=missing", StringComparison.Ordinal)
            .Replace("docker=available:24.0.2", "docker=unavailable", StringComparison.Ordinal)
            .Replace("containers=available", "containers=unavailable", StringComparison.Ordinal);
        var facts = NasPreflightProtocol.Parse(0, output);
        Assert.Equal(PreflightFactStatus.Missing, facts.Dsm.Status);
        Assert.Equal(PreflightFactStatus.Unavailable, facts.DockerServer.Status);
        Assert.Null(facts.DockerServer.Version);
        Assert.Contains(NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts)),
            check => check.Code == "preflight.containers.unavailable" && check.Status == PreflightCheckStatus.Blocking);
    }

    [Theory]
    [InlineData("hh-homelab-wizard:nas-preflight:v2", "unexpected-marker")]
    [InlineData("hh-homelab-wizard:nas-preflight:v2", "hh-homelab-wizard:nas-preflight:v1")]
    [InlineData("model=available:DS725+", "model=available:")]
    [InlineData("model=available:DS725+", "model=available:DS 725")]
    [InlineData("model=available:DS725+", "model=unknown")]
    [InlineData("listening=available", "listening=missing")]
    [InlineData("listen=22", "listen=0")]
    [InlineData("listen=22", "listen=65536")]
    [InlineData("listen=22", "listen=-22")]
    [InlineData("listen=22", "listen=5000")]
    [InlineData("listening=available\nlisten=22", "listening=unavailable\nlisten=22")]
    [InlineData("volumes=available", "volumes=unknown")]
    [InlineData("volume=/volume1:7810000000:5000000000", "volume=/volume1:7810000000:9000000000")]
    [InlineData("volume=/volume1:7810000000:5000000000", "volume=/volume1:0:0")]
    [InlineData("volume=/volume1:7810000000:5000000000", "volume=/volumeUSB1/usbshare:100:10")]
    [InlineData("volume=/volume1:7810000000:5000000000", "volume=/volume01:100:10")]
    [InlineData("volume=/volume1:7810000000:5000000000", "volume=/volume1:100")]
    [InlineData("volume=/volume1:7810000000:5000000000", "volume=/volume1:100:10\nvolume=/volume1:100:10")]
    [InlineData("volumes=available\nvolume=/volume1:7810000000:5000000000", "volumes=unavailable\nvolume=/volume1:7810000000:5000000000")]
    [InlineData("listening=available\nlisten=22\nlisten=5000\nlisten=5001\n", "")]
    [InlineData("volumes=available\n", "")]
    [InlineData("kernel=Linux", "kernel=Linux\nextra=ignored")]
    [InlineData("architecture=x86_64", "architecture=x86_64\u0000")]
    [InlineData("unix-time=1792368000", "unix-time=-1")]
    [InlineData("unix-time=1792368000", "unix-time=999999999999999999")]
    [InlineData("dsm=available:7.4.1-90080", "dsm=available:")]
    [InlineData("dsm=available:7.4.1-90080", "dsm=available:7.4.1;payload")]
    [InlineData("dsm=available:7.4.1-90080", "dsm=unknown")]
    [InlineData("container-manager=available:24.0.2-1535", "dsm=missing")]
    [InlineData("containers=available", "containers=missing")]
    [InlineData("containers=available", "containers=available\ncontainer=short-id")]
    [InlineData("docker=available:24.0.2", "docker=missing")]
    [InlineData("end", "end\ntrailer")]
    public void MalformedOrInconsistentOutputIsRejectedWithoutReturningPartialFacts(string original, string replacement)
    {
        var output = PreflightData.Output.Replace(original, replacement, StringComparison.Ordinal);
        var error = Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, output));
        Assert.Equal("preflight.response.invalid", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(126)]
    public void CompleteOutputWithUnsuccessfulExitIsStillRejected(int? exitCode)
    {
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(exitCode, PreflightData.Output));
    }

    [Fact]
    public void InventoryIsBoundedAndDuplicatesOrPartialInventoriesAreNeverAnEmptySuccess()
    {
        var ids = Enumerable.Range(0, 128).Select(index => index.ToString("x64")).ToArray();
        var output = PreflightData.WithContainers(ids);
        var facts = NasPreflightProtocol.Parse(0, output);
        Assert.Equal(128, facts.ContainerIds.Count);
        Assert.Contains(NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts)),
            check => check.Code == "preflight.containers.ownership-required" && check.Status == PreflightCheckStatus.Blocking);
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, PreflightData.WithContainers(ids.Append(new string('f', 64)))));
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, PreflightData.WithContainers([new string('a', 64), new string('A', 64)])));
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, output.Replace("containers=available", "containers=unavailable", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, new string('x', 17000)));
    }

    [Fact]
    public void FactsCopyAndCanonicalizeContainerIdsAndDoNotExposeAMutableArray()
    {
        var ids = new[] { new string('B', 64), new string('a', 64) };
        var facts = PreflightData.Facts(ids);
        ids[0] = new string('c', 64);
        Assert.Equal(new[] { new string('a', 64), new string('b', 64) }, facts.ContainerIds);
        var collection = Assert.IsAssignableFrom<IList<string>>(facts.ContainerIds);
        Assert.Throws<NotSupportedException>(() => collection[0] = new string('d', 64));
    }

    [Fact]
    public void EnvironmentFingerprintIgnoresClockTickButDetectsIdentityVersionOrResourceDrift()
    {
        var snapshot = PreflightData.Snapshot(PreflightData.Facts([new string('a', 64)]));
        var nextTime = PreflightData.Now.AddSeconds(10);
        var next = new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, nextTime, nextTime,
            PreflightData.Facts([new string('a', 64)], nextTime));
        Assert.Equal(snapshot.EnvironmentFingerprint, next.EnvironmentFingerprint);
        Assert.NotEqual(snapshot.EnvironmentFingerprint, PreflightData.Snapshot(PreflightData.Facts([new string('b', 64)])).EnvironmentFingerprint);
        var changed = NasPreflightProtocol.Parse(0, PreflightData.Output.Replace("docker=available:24.0.2", "docker=available:25.0.0", StringComparison.Ordinal));
        Assert.NotEqual(PreflightData.Snapshot(PreflightData.Facts()).EnvironmentFingerprint, PreflightData.Snapshot(changed).EnvironmentFingerprint);
        var rotated = PreflightData.Snapshot(PreflightData.Facts(), PreflightData.OtherPin);
        Assert.NotEqual(PreflightData.Snapshot(PreflightData.Facts()).EnvironmentFingerprint, rotated.EnvironmentFingerprint);
    }

    [Fact]
    public void FreshnessRequiresSameHostKeyProfileAndAnEntireObservationInsideTheAgeWindow()
    {
        var snapshot = PreflightData.Snapshot(PreflightData.Facts());
        var age = TimeSpan.FromMinutes(2);
        Assert.True(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddSeconds(1), age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.OtherPin, PreflightData.Profile, PreflightData.Now, age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, new string('b', 64), PreflightData.Now, age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddSeconds(-1), age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddMinutes(3), age));
        Assert.False(snapshot.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, TimeSpan.FromDays(1)));
        var otherUser = PreflightData.Target with { Username = "other-synthetic-user" };
        Assert.False(snapshot.IsCurrentFor(otherUser, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, age));
        var slow = new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddMinutes(-3), PreflightData.Now, snapshot.Facts);
        Assert.False(slow.IsCurrentFor(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, age));
    }

    [Theory]
    [InlineData("Darwin", "x86_64", 0, "preflight.kernel")]
    [InlineData("Linux", "aarch64", 0, "preflight.architecture")]
    [InlineData("Linux", "x86_64", 121, "preflight.clock-window")]
    [InlineData("Linux", "x86_64", -121, "preflight.clock-window")]
    public void WrongPlatformOrClockSkewBlocksPrerequisites(string kernel, string architecture, int offset, string code)
    {
        var facts = PreflightData.Facts(kernel: kernel, architecture: architecture, serverTime: PreflightData.Now.AddSeconds(offset));
        var checks = NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts));
        Assert.Contains(checks, check => check.Code == code && check.Status == PreflightCheckStatus.Blocking);
    }

    [Fact]
    public void ObservedHostResourcesWithoutPlanRequirementsDoNotBecomePassedChecks()
    {
        var facts = NasPreflightProtocol.Parse(0, PreflightData.Output);
        var resources = facts.Resources;
        Assert.Equal(new PreflightModel(PreflightFactStatus.Available, "DS725+"), resources.Model);
        Assert.Equal(new[] { 22, 5000, 5001 }, resources.ListeningTcpPorts);
        Assert.Equal(PreflightData.Volume1, Assert.Single(resources.Volumes));
        var checks = NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts));
        Assert.Contains(checks, check => check.Code == "preflight.nas-model" && check.Status == PreflightCheckStatus.Unverified);
        Assert.Contains(checks, check => check.Code == "preflight.ports" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Contains(checks, check => check.Code == "preflight.disk-space" && check.Status == PreflightCheckStatus.NotChecked);
    }

    [Fact]
    public void FreeRequiredPortsAndSufficientSelectedVolumePassOnlyTheirOwnChecks()
    {
        var snapshot = PreflightData.Snapshot(PreflightData.Facts());
        var requirements = new PreflightRequirements([8080, 8123], "/volume1", 100L * 1024 * 1024 * 1024);
        var checks = NasPreflightAssessment.Evaluate(snapshot, requirements);
        Assert.Contains(checks, check => check.Code == "preflight.ports" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "preflight.disk-space" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "preflight.paths-acl" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Contains(checks, check => check.Code == "preflight.ownership" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Empty(NasPreflightAssessment.ConflictingPorts(snapshot, requirements));
    }

    [Fact]
    public void ListenerOnRequiredPortBlocksAndNamesOnlyConflictingPorts()
    {
        var snapshot = PreflightData.Snapshot(PreflightData.Facts(resources: PreflightData.Resources([22, 8080, 8123])));
        var requirements = new PreflightRequirements([9696, 8123, 8080], "/volume1", 1);
        Assert.Contains(NasPreflightAssessment.Evaluate(snapshot, requirements),
            check => check.Code == "preflight.ports.conflict" && check.Status == PreflightCheckStatus.Blocking);
        Assert.Equal(new[] { 8080, 8123 }, NasPreflightAssessment.ConflictingPorts(snapshot, requirements));
    }

    [Theory]
    [InlineData("/volume2", 1L, "preflight.disk-space.volume-missing")]
    [InlineData("/volume1", 5000000000L * 1024 + 1, "preflight.disk-space.insufficient")]
    public void MissingSelectedVolumeOrInsufficientSpaceBlocks(string mount, long required, string code)
    {
        var snapshot = PreflightData.Snapshot(PreflightData.Facts());
        var checks = NasPreflightAssessment.Evaluate(snapshot, new([8080], mount, required));
        Assert.Contains(checks, check => check.Code == code && check.Status == PreflightCheckStatus.Blocking);
        Assert.DoesNotContain(checks, check => check.Code == "preflight.disk-space" && check.Status == PreflightCheckStatus.Passed);
    }

    [Fact]
    public void UnreadableListenersOrVolumesBlockInsteadOfLookingFree()
    {
        var output = PreflightData.Output
            .Replace("listening=available\nlisten=22\nlisten=5000\nlisten=5001\n", "listening=unavailable\n", StringComparison.Ordinal)
            .Replace("volumes=available\nvolume=/volume1:7810000000:5000000000\n", "volumes=unavailable\n", StringComparison.Ordinal)
            .Replace("model=available:DS725+", "model=unavailable", StringComparison.Ordinal);
        var facts = NasPreflightProtocol.Parse(0, output);
        Assert.Empty(facts.Resources.ListeningTcpPorts);
        Assert.Empty(facts.Resources.Volumes);
        var checks = NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts), new([8080], "/volume1", 1));
        Assert.Contains(checks, check => check.Code == "preflight.ports.unavailable" && check.Status == PreflightCheckStatus.Blocking);
        Assert.Contains(checks, check => check.Code == "preflight.disk-space.unavailable" && check.Status == PreflightCheckStatus.Blocking);
        Assert.Contains(checks, check => check.Code == "preflight.nas-model" && check.Status == PreflightCheckStatus.Blocking);
    }

    [Fact]
    public void EmptyObservedListenersAndNonDsmVolumesAreValidButSelectedVolumeStaysMissing()
    {
        var output = PreflightData.WithPorts([]).Replace("volume=/volume1:7810000000:5000000000\n", "", StringComparison.Ordinal);
        var facts = NasPreflightProtocol.Parse(0, output);
        Assert.Equal(PreflightFactStatus.Available, facts.Resources.ListeningStatus);
        Assert.Empty(facts.Resources.ListeningTcpPorts);
        var checks = NasPreflightAssessment.Evaluate(PreflightData.Snapshot(facts), new([8080], "/volume1", 1));
        Assert.Contains(checks, check => check.Code == "preflight.ports" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "preflight.disk-space.volume-missing" && check.Status == PreflightCheckStatus.Blocking);
    }

    [Fact]
    public void ResourceListsAreBoundedAndTheLargestAcceptedResponseFitsTheSshOutputLimit()
    {
        var ids = Enumerable.Range(0, NasPreflightFacts.MaximumContainers).Select(index => index.ToString("x64"));
        var ports = Enumerable.Range(65535 - NasHostResources.MaximumListeningPorts + 1, NasHostResources.MaximumListeningPorts).ToArray();
        var volumes = Enumerable.Range(1, NasHostResources.MaximumVolumes)
            .Select(index => "volume=/volume" + (index * 10) + ":999999999999999:999999999999999\n");
        var largest = PreflightData.WithPorts(ports).Replace("volume=/volume1:7810000000:5000000000\n", string.Concat(volumes), StringComparison.Ordinal);
        largest = largest.Replace("containers=available\n", "containers=available\n" + string.Concat(ids.Select(id => "container=" + id + "\n")), StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(largest) <= SshProbeOutput.MaximumBytes);
        var facts = NasPreflightProtocol.Parse(0, largest);
        Assert.Equal(NasHostResources.MaximumListeningPorts, facts.Resources.ListeningTcpPorts.Count);
        Assert.Equal(NasHostResources.MaximumVolumes, facts.Resources.Volumes.Count);
        Assert.Equal("/volume10", facts.Resources.Volumes[0].Mount);
        Assert.Equal("/volume160", facts.Resources.Volumes[^1].Mount);
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0, PreflightData.WithPorts(ports.Append(1))));
        var tooManyVolumes = Enumerable.Range(1, NasHostResources.MaximumVolumes + 1).Select(index => "volume=/volume" + index + ":10:1\n");
        Assert.Throws<InvalidDataException>(() => NasPreflightProtocol.Parse(0,
            PreflightData.Output.Replace("volume=/volume1:7810000000:5000000000\n", string.Concat(tooManyVolumes), StringComparison.Ordinal)));
    }

    [Fact]
    public void ListenerOrVolumeLayoutChangeIsEnvironmentDriftButFreeSpaceMovementIsNot()
    {
        var baseline = PreflightData.Snapshot(PreflightData.Facts()).EnvironmentFingerprint;
        var lessSpace = PreflightData.Resources(volumes: [PreflightData.Volume1 with { AvailableBytes = 1024 }]);
        Assert.Equal(baseline, PreflightData.Snapshot(PreflightData.Facts(resources: lessSpace)).EnvironmentFingerprint);
        var newListener = PreflightData.Resources([22, 5000, 5001, 8080]);
        Assert.NotEqual(baseline, PreflightData.Snapshot(PreflightData.Facts(resources: newListener)).EnvironmentFingerprint);
        var resized = PreflightData.Resources(volumes: [PreflightData.Volume1 with { TotalBytes = PreflightData.Volume1.TotalBytes * 2 }]);
        Assert.NotEqual(baseline, PreflightData.Snapshot(PreflightData.Facts(resources: resized)).EnvironmentFingerprint);
        var otherModel = PreflightData.Resources(model: "DS925+");
        Assert.NotEqual(baseline, PreflightData.Snapshot(PreflightData.Facts(resources: otherModel)).EnvironmentFingerprint);
    }

    [Fact]
    public void RequirementsRejectInvalidPortsVolumesAndSpace()
    {
        Assert.Throws<ArgumentException>(() => new PreflightRequirements([0], "/volume1", 1));
        Assert.Throws<ArgumentException>(() => new PreflightRequirements([8080, 8080], "/volume1", 1));
        Assert.Throws<ArgumentException>(() => new PreflightRequirements(Enumerable.Range(1, PreflightRequirements.MaximumPorts + 1), "/volume1", 1));
        Assert.Throws<ArgumentException>(() => new PreflightRequirements([8080], "/volume1/data", 1));
        Assert.Throws<ArgumentException>(() => new PreflightRequirements([8080], "/volume1", 0));
        var ports = new List<int> { 8123, 8080 };
        var requirements = new PreflightRequirements(ports, "/volume1", 1);
        ports.Add(9696);
        Assert.Equal(new[] { 8080, 8123 }, requirements.TcpPorts);
        Assert.False(new PreflightModel(PreflightFactStatus.Missing, "DS725+").IsValid);
        Assert.False(new PreflightVolume("/volume1", 10, 11).IsValid);
    }

    [Fact]
    public void DomainRejectsInvalidBindingsAndReversedObservationWindow()
    {
        Assert.Throws<ArgumentException>(() => new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, "invalid", PreflightData.Now, PreflightData.Now, PreflightData.Facts()));
        Assert.Throws<ArgumentException>(() => new NasPreflightSnapshot(PreflightData.Target, PreflightData.Pin, PreflightData.Profile, PreflightData.Now.AddSeconds(1), PreflightData.Now, PreflightData.Facts()));
        Assert.Throws<ArgumentException>(() => NasPreflightResult.Failed(SshProbeResult.Ready(PreflightData.Pin, new(1000, false))));
        Assert.False(new PreflightVersion((PreflightFactStatus)100).IsValid);
        Assert.False(new PreflightVersion(PreflightFactStatus.Missing, "24.0.2").IsValid);
    }
}

internal static class PreflightData
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1792368000);

    internal static readonly string Profile = new('a', 64);

    internal static readonly SshConnectionTarget Target = new("nas.example.invalid", 22, "synthetic-user");

    internal static readonly byte[] PublicKey = Encoding.UTF8.GetBytes("synthetic-preflight-public-key");

    internal static SshHostKey Pin => SshHostKey.FromPublicKey(Target.EndpointFingerprint, "ssh-ed25519", PublicKey);
    internal static SshHostKey OtherPin => SshHostKey.FromPublicKey(Target.EndpointFingerprint, "ssh-ed25519", [1, 2, 3]);

    internal const string Output = "hh-homelab-wizard:nas-preflight:v2\nkernel=Linux\narchitecture=x86_64\nunix-time=1792368000\ndsm=available:7.4.1-90080\ncontainer-manager=available:24.0.2-1535\nmodel=available:DS725+\ndocker=available:24.0.2\ncompose=available:2.20.3\ncontainers=available\nlistening=available\nlisten=22\nlisten=5000\nlisten=5001\nvolumes=available\nvolume=/volume1:7810000000:5000000000\nend\n";

    internal static string WithContainers(IEnumerable<string> ids) => Output.Replace("containers=available\n",
        "containers=available\n" + string.Concat(ids.Select(id => "container=" + id + "\n")), StringComparison.Ordinal);

    internal static string WithPorts(IEnumerable<int> ports) => Output.Replace("listen=22\nlisten=5000\nlisten=5001\n",
        string.Concat(ports.Select(port => "listen=" + port + "\n")), StringComparison.Ordinal);

    internal static NasHostResources Resources(IEnumerable<int>? ports = null, IEnumerable<PreflightVolume>? volumes = null,
        string model = "DS725+") => new(new(PreflightFactStatus.Available, model), PreflightFactStatus.Available,
            ports ?? [22, 5000, 5001], PreflightFactStatus.Available, volumes ?? [Volume1]);

    internal static readonly PreflightVolume Volume1 = new("/volume1", 7810000000L * 1024, 5000000000L * 1024);

    internal static NasPreflightFacts Facts(IEnumerable<string>? ids = null, DateTimeOffset? serverTime = null,
        string kernel = "Linux", string architecture = "x86_64", NasHostResources? resources = null) => new(kernel, architecture, serverTime ?? Now,
            new(PreflightFactStatus.Available, "7.4.1-90080"), new(PreflightFactStatus.Available, "24.0.2-1535"),
            new(PreflightFactStatus.Available, "24.0.2"), new(PreflightFactStatus.Available, "2.20.3"), PreflightFactStatus.Available, ids ?? [], resources ?? Resources());

    internal static NasPreflightSnapshot Snapshot(NasPreflightFacts facts, SshHostKey? pin = null) => new(Target, pin ?? Pin, Profile, Now, Now, facts);
}
