using System.Diagnostics;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

/// <summary>Runs the real nas-compose-read.sh under /bin/sh against a fake docker CLI that answers from files,
/// so label comparison, failure handling and the read-only command set are checked without a daemon.</summary>
public sealed class ComposeReadScriptTests : IDisposable
{
    private const string Project = "hhw-b3d64c85e3914960b58ac157d74acb62";

    private const string Directory = "/volume1/docker/hh-homelab/media";

    private const string FakeDocker = """
        #!/bin/sh
        PATH=/usr/bin:/bin
        state="$FAKE_DOCKER_STATE"
        printf '%s|%s\n' "${DOCKER_HOST:-}" "$*" >> "$state/calls"
        [ "$1" = --host ] && [ "$2" = unix:///var/run/docker.sock ] || exit 99
        kind="$3"
        verb="$4"
        case "$verb" in
            ls)
                [ ! -e "$state/$kind-ls.fail" ] || exit 1
                cat "$state/$kind-ls"
                ;;
            inspect)
                [ ! -e "$state/inspect.fail" ] || exit 1
                format="$6"
                id="$7"
                [ -d "$state/$kind/$id" ] || exit 1
                if [ "$format" = '{{.State.Status}}' ]; then
                    cat "$state/$kind/$id/state"
                    exit 0
                fi
                key="${format#*\"}"
                key="${key%%\"*}"
                [ ! -e "$state/$kind/$id/$key" ] || cat "$state/$kind/$id/$key"
                printf '.'
                ;;
            *) exit 98 ;;
        esac
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), "wizard-compose-read-" + Guid.NewGuid().ToString("N"));

    private readonly string bin;

    private readonly string state;

    public ComposeReadScriptTests()
    {
        bin = System.IO.Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        state = System.IO.Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        File.WriteAllText(Path.Combine(state, "network-ls"), "");
        File.WriteAllText(Path.Combine(state, "container-ls"), "");
        File.WriteAllText(Path.Combine(state, "calls"), "");
    }

    public static bool ShellUnavailable => !OperatingSystem.IsLinux();

    private static string NetworkId => new('a', 64);

    private static string ContainerId => new('c', 64);

    private static ComposeProjectIdentity Identity => new(Project, "media", "media-test", "qbittorrent", "qbittorrent", Directory);

    [Fact(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    public async Task NoMatchingNameIsAbsentAndOnlyListsAndInspectsAreUsed()
    {
        var output = await RunAsync();

        Assert.Equal(ComposeProjectState.Absent, NasComposeProjectProtocol.Parse(0, output).State);
        AssertReadOnlyCalls();
    }

    [Fact(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    public async Task ResourcesWithThisProjectsLabelsAreOwn()
    {
        AddNetwork(NetworkId, Project, "media");
        AddContainer(ContainerId, Project, "qbittorrent", "False", Directory, "running");

        var facts = NasComposeProjectProtocol.Parse(0, await RunAsync());

        Assert.Equal(ComposeProjectState.Owned, facts.State);
        Assert.Equal(ComposeContainerState.Running, facts.OwnContainer!.State);
        AssertReadOnlyCalls();
    }

    [Theory(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    [InlineData("project")]
    [InlineData("service")]
    [InlineData("oneoff")]
    [InlineData("directory")]
    [InlineData("unlabelled")]
    public async Task AnyDifferingContainerLabelIsForeign(string difference)
    {
        AddNetwork(NetworkId, Project, "media");
        var project = difference == "project" ? "hhw-00000000000000000000000000000000" : Project;
        var service = difference == "service" ? "qbittorrent2" : "qbittorrent";
        var oneoff = difference == "oneoff" ? "True" : "False";
        var directory = difference == "directory" ? "/volume1/other/media" : Directory;
        if (difference == "unlabelled")
            AddContainer(ContainerId, null, null, null, null, "created");
        else
            AddContainer(ContainerId, project, service, oneoff, directory, "created");

        var facts = NasComposeProjectProtocol.Parse(0, await RunAsync());

        Assert.Equal(ComposeProjectState.Foreign, facts.State);
        Assert.Equal(ComposeResourceOwnership.Foreign, Assert.Single(facts.Containers).Ownership);
    }

    [Theory(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    [InlineData("\n")]
    [InlineData("\n\n")]
    [InlineData(" ")]
    public async Task LabelWithAppendedWhitespaceIsNotThisProject(string suffix)
    {
        AddNetwork(NetworkId, Project + suffix, "media");

        var facts = NasComposeProjectProtocol.Parse(0, await RunAsync());

        Assert.Equal(ComposeProjectState.Foreign, facts.State);
    }

    [Fact(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    public async Task NetworkOfAnotherComposeKeyIsForeign()
    {
        AddNetwork(NetworkId, Project, "default");

        var facts = NasComposeProjectProtocol.Parse(0, await RunAsync());

        Assert.Equal(ComposeProjectState.Foreign, facts.State);
    }

    [Theory(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    [InlineData("network-ls")]
    [InlineData("container-ls")]
    [InlineData("inspect")]
    [InlineData("unknown-state")]
    [InlineData("short-id")]
    [InlineData("too-many")]
    [InlineData("no-docker")]
    public async Task EveryReadFailureIsUnavailableWithoutPartialRecords(string failure)
    {
        AddNetwork(NetworkId, Project, "media");
        AddContainer(ContainerId, Project, "qbittorrent", "False", Directory, failure == "unknown-state" ? "healthy" : "running");
        if (failure is "network-ls" or "container-ls")
            File.WriteAllText(Path.Combine(state, failure + ".fail"), "");
        if (failure == "inspect")
            File.WriteAllText(Path.Combine(state, "inspect.fail"), "");
        if (failure == "short-id")
            File.WriteAllText(Path.Combine(state, "container-ls"), ContainerId[..12] + "\n");
        if (failure == "too-many")
        {
            foreach (var number in Enumerable.Range(1, ComposeProjectFacts.MaximumMatches))
                AddNetwork(number.ToString("x64"), Project, "media");
        }

        var output = await RunAsync(withDocker: failure != "no-docker");

        Assert.Equal("hh-homelab-wizard:nas-compose:v1\nstatus=unavailable\nend\n", output);
        Assert.Equal(ComposeProjectState.Unavailable, NasComposeProjectProtocol.Parse(0, output).State);
    }

    [Fact(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    public async Task NonMatchingListEntriesAreSkipped()
    {
        // The name filter of docker ls matches substrings; the template prints an empty line for those.
        File.WriteAllText(Path.Combine(state, "network-ls"), "\n\n");
        File.WriteAllText(Path.Combine(state, "container-ls"), "\n");

        var facts = NasComposeProjectProtocol.Parse(0, await RunAsync());

        Assert.Equal(ComposeProjectState.Absent, facts.State);
    }

    [Fact(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    public async Task AmbientDockerEndpointIsIgnored()
    {
        await RunAsync(environment: new() { ["DOCKER_HOST"] = "tcp://127.0.0.1:1" });

        var calls = File.ReadAllLines(Path.Combine(state, "calls"));
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.StartsWith("|--host unix:///var/run/docker.sock ", call, StringComparison.Ordinal));
    }

    [Theory(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    [InlineData("project=HHW-B3D64C85E3914960B58AC157D74ACB62")]
    [InlineData("network=media\"test")]
    [InlineData("network=media.test")]
    [InlineData("container={{.ID}}")]
    [InlineData("directory=/tmp/media")]
    public async Task InvalidRequestStopsBeforeDocker(string replacement)
    {
        var field = replacement[..replacement.IndexOf('=', StringComparison.Ordinal)];
        var request = string.Join('\n', NasComposeProjectProtocol.Request(Identity).Split('\n')
            .Select(line => line.StartsWith(field + "=", StringComparison.Ordinal) ? replacement : line));

        var (exitCode, output) = await RunRawAsync(request, withDocker: true, environment: null);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Empty(File.ReadAllText(Path.Combine(state, "calls")));
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(root))
            System.IO.Directory.Delete(root, recursive: true);
    }

    private void AddNetwork(string id, string project, string key)
    {
        AppendLine("network-ls", id);
        var directory = System.IO.Directory.CreateDirectory(Path.Combine(state, "network", id)).FullName;
        File.WriteAllText(Path.Combine(directory, "com.docker.compose.project"), project);
        File.WriteAllText(Path.Combine(directory, "com.docker.compose.network"), key);
    }

    private void AddContainer(string id, string? project, string? service, string? oneoff, string? directory, string status)
    {
        AppendLine("container-ls", id);
        var labels = System.IO.Directory.CreateDirectory(Path.Combine(state, "container", id)).FullName;
        WriteLabel(labels, "com.docker.compose.project", project);
        WriteLabel(labels, "com.docker.compose.service", service);
        WriteLabel(labels, "com.docker.compose.oneoff", oneoff);
        WriteLabel(labels, "com.docker.compose.project.working_dir", directory);
        File.WriteAllText(Path.Combine(labels, "state"), status + "\n");
    }

    private static void WriteLabel(string directory, string key, string? value)
    {
        if (value is not null)
            File.WriteAllText(Path.Combine(directory, key), value);
    }

    private void AppendLine(string file, string line) => File.AppendAllText(Path.Combine(state, file), line + "\n");

    private void AssertReadOnlyCalls()
    {
        var calls = File.ReadAllLines(Path.Combine(state, "calls"));
        Assert.All(calls, call =>
        {
            var arguments = call.Split(' ');
            Assert.Contains(arguments[3], new[] { "ls", "inspect" });
        });
    }

    private async Task<string> RunAsync(bool withDocker = true, Dictionary<string, string>? environment = null)
    {
        var (exitCode, output) = await RunRawAsync(NasComposeProjectProtocol.Request(Identity), withDocker, environment);
        Assert.Equal(0, exitCode);
        return output;
    }

    private async Task<(int ExitCode, string Output)> RunRawAsync(string request, bool withDocker, Dictionary<string, string>? environment)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("test.linux.required");

        if (withDocker)
        {
            var docker = Path.Combine(bin, "docker");
            await File.WriteAllTextAsync(docker, FakeDocker + "\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(NasComposeProjectProtocol.Command);
        // Only the fake docker is reachable: the script needs no other external command.
        start.Environment["PATH"] = bin;
        start.Environment["FAKE_DOCKER_STATE"] = state;
        foreach (var (name, value) in environment ?? [])
            start.Environment[name] = value;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        try
        {
            await process.StandardInput.WriteAsync(request);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, output);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
