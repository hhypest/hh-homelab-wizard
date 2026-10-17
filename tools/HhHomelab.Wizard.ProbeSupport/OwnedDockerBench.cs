using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HhHomelab.Wizard.ProbeSupport;

/// <summary>Local developer fixture. Never attaches to an existing container or host directory.</summary>
public sealed partial class OwnedDockerBench : IAsyncDisposable
{
    private const string Label = "com.hh-homelab-wizard.session";
    private readonly string session = "m0-media-" + Guid.NewGuid().ToString("N");
    private readonly string context;
    private string Container => session + "-app";
    private string Network => session + "-net";
    private string Volume => session + "-config";
    private readonly List<(string Kind, string Name)> resources = [];
    private readonly HashSet<string> volumes = [];
    private readonly Dictionary<string, (string Container, Uri Endpoint, int Port)> services = [];
    public Uri Endpoint { get; private set; } = null!;
    public bool CleanupSucceeded { get; private set; }

    private OwnedDockerBench(string context) => this.context = context;

    public static async Task<OwnedDockerBench> ConnectAsync(CancellationToken cancellationToken)
    {
        // Refuse environment overrides and remote daemons; this tool is never a NAS installer.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            throw new InvalidOperationException("bench.remote-docker.forbidden");
        var selected = await RunProcessAsync(["context", "show"], cancellationToken);
        if (selected.ExitCode != 0)
            throw new InvalidOperationException("bench.docker.unavailable");
        var context = selected.Output.Trim();
        if (!ContextPattern().IsMatch(context))
            throw new InvalidOperationException("bench.context.invalid");
        var inspected = await RunProcessAsync(["context", "inspect", context], cancellationToken);
        if (inspected.ExitCode != 0)
            throw new InvalidOperationException("bench.context.unavailable");
        using var json = JsonDocument.Parse(inspected.Output);
        var host = json.RootElement[0]
            .GetProperty("Endpoints")
            .GetProperty("docker")
            .GetProperty("Host")
            .GetString();
        if (
            host is null
            || !(
                host.StartsWith("npipe:////./pipe/", StringComparison.Ordinal)
                || host.StartsWith("unix:///", StringComparison.Ordinal)
            )
        )
            throw new InvalidOperationException("bench.remote-docker.forbidden");
        var bench = new OwnedDockerBench(context);
        var info = await bench.RunAsync(["info", "--format", "{{.OSType}}"], cancellationToken);
        if (info.Trim() != "linux")
            throw new InvalidOperationException("bench.linux-engine.required");
        return bench;
    }

    public async Task StartAsync(string image, CancellationToken cancellationToken)
    {
        if (resources.Count != 0)
            throw new InvalidOperationException("bench.mode.conflict");
        // Require a local image. No unbounded pull/install steps or Docker mods in this API probe.
        _ = await RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], cancellationToken);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        await CreateNetworkAsync(cancellationToken);
        await CreateVolumeAsync(Volume, cancellationToken);
        resources.Add(("container", Container));
        _ = await RunAsync(
            [
                "create",
                "--name",
                Container,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--publish",
                $"127.0.0.1:{port}:{port}",
                "--env",
                "WEBUI_PORT=" + port,
                "--env",
                "PUID=1000",
                "--env",
                "PGID=1000",
                "--mount",
                $"type=volume,source={Volume},target=/config",
                "--tmpfs",
                "/data:rw,uid=1000,gid=1000,mode=0755",
                "--log-driver",
                "json-file",
                "--log-opt",
                "max-size=1m",
                "--log-opt",
                "max-file=1",
                image,
            ],
            cancellationToken
        );
        // A port reservation race fails container start and is cleaned up, never reassigned silently.
        _ = await RunAsync(["start", Container], cancellationToken);
        await WaitForApiAsync(cancellationToken);
    }

    public async Task<string> ReadTemporaryPasswordAsync(CancellationToken cancellationToken)
    {
        // Application logs contain its generated temporary password. Capture only into memory;
        // never publish raw logs or put credentials into Docker/process arguments.
        var logs = await RunAsync(["logs", Container], cancellationToken, includeError: true);
        var match = TemporaryPasswordPattern().Match(logs);
        if (!match.Success)
            throw new InvalidOperationException("bench.temporary-password.unavailable");
        return match.Groups[1].Value.Trim();
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await VerifyOwnershipAsync("container", Container, cancellationToken);
        _ = await RunAsync(["stop", "--time", "10", Container], cancellationToken);
        _ = await RunAsync(["start", Container], cancellationToken);
        await WaitForApiAsync(cancellationToken);
    }

    private async Task WaitForApiAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = Endpoint,
            Timeout = TimeSpan.FromSeconds(3),
        };
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version");
                request.Headers.Referrer = Endpoint;
                using var response = await client.SendAsync(request, deadline.Token);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("bench.http-policy.rejected");
                if (response.StatusCode == HttpStatusCode.OK)
                    throw new InvalidOperationException("bench.authentication-bypass.detected");
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(500, deadline.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var succeeded = true;
        // Cleanup has its own bounded tokens, independent of a cancelled scenario.
        foreach (var (kind, name) in resources.AsEnumerable().Reverse())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var arguments = new List<string>
                {
                    kind,
                    "ls",
                    "--filter",
                    "name=" + name,
                    "--format",
                    "{{.Name}}",
                };
                if (kind == "container")
                {
                    arguments.Insert(2, "--all");
                    arguments[^1] = "{{.Names}}";
                }
                var names = await RunAsync(arguments, deadline.Token);
                if (
                    !names
                        .Split('\n', StringSplitOptions.TrimEntries)
                        .Contains(name, StringComparer.Ordinal)
                )
                    continue;
                await VerifyOwnershipAsync(kind, name, deadline.Token);
                var remove = new List<string> { kind, "rm" };
                if (kind == "container")
                    remove.Add("--force");
                remove.Add(name);
                _ = await RunAsync(remove, deadline.Token);
            }
            catch (Exception)
            {
                succeeded = false;
                Console.Error.WriteLine("bench.cleanup.failed: " + kind + "; session=" + session);
            }
        }
        CleanupSucceeded = succeeded;
        if (succeeded)
            Console.WriteLine("bench.cleanup: passed");
    }

    private async Task VerifyOwnershipAsync(
        string kind,
        string name,
        CancellationToken cancellationToken
    )
    {
        using var json = JsonDocument.Parse(
            await RunAsync([kind, "inspect", name], cancellationToken)
        );
        var root = json.RootElement[0];
        var labels =
            kind == "container"
                ? root.GetProperty("Config").GetProperty("Labels")
                : root.GetProperty("Labels");
        if (labels.GetProperty(Label).GetString() != session)
            throw new InvalidOperationException("bench.ownership.failed");
        if (kind == "container")
        {
            foreach (var mount in root.GetProperty("Mounts").EnumerateArray())
                if (
                    mount.GetProperty("Type").GetString() == "bind"
                    || mount.GetProperty("Type").GetString() == "volume"
                        && !volumes.Contains(mount.GetProperty("Name").GetString()!)
                )
                    throw new InvalidOperationException("bench.mounts.foreign");
        }
    }

    private async Task<string> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        bool includeError = false
    )
    {
        var result = await RunProcessAsync(
            new[] { "--context", context }.Concat(arguments),
            cancellationToken
        );
        if (result.ExitCode != 0)
            throw new InvalidOperationException("bench.docker." + arguments.First() + ".failed");
        return result.Output + (includeError ? result.Error : "");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        Stream? input = null,
        TimeSpan? timeout = null
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process =
            Process.Start(start) ?? throw new InvalidOperationException("bench.docker.unavailable");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            if (input is not null)
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            // Observe outstanding reads before disposal; never print either stream.
            try
            {
                await Task.WhenAll(output, error);
            }
            catch (OperationCanceledException) { }
            throw;
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContextPattern();

    [GeneratedRegex(@"temporary password[^\r\n]*: ([^\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryPasswordPattern();
}
