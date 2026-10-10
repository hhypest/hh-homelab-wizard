using System.Net;
using System.Net.Sockets;
using System.Xml;

namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    private string DataVolume => session + "-data";
    public Uri GetEndpoint(string id) => services[id].Endpoint;
    public Uri GetInternalEndpoint(string id) => new($"http://{id}:{services[id].Port}/");

    private async Task CreateNetworkAsync(CancellationToken token)
    {
        resources.Add(("network", Network));
        _ = await RunAsync(["network", "create", "--label", Label + "=" + session, Network], token);
    }
    private async Task CreateVolumeAsync(string name, CancellationToken token)
    {
        resources.Add(("volume", name)); volumes.Add(name);
        _ = await RunAsync(["volume", "create", "--label", Label + "=" + session, name], token);
    }

    public async Task StartMediaServiceAsync(string id, string image, int port, CancellationToken token)
    {
        if (id is not ("qbittorrent" or "radarr" or "sonarr" or "prowlarr" or "jellyfin" or "seerr") || services.ContainsKey(id) || port is < 1 or > 65535)
            throw new InvalidOperationException("bench.service.invalid");
        if (resources.Count != 0 && services.Count == 0) throw new InvalidOperationException("bench.mode.conflict");
        _ = await RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], token);
        if (resources.Count == 0)
        {
            await CreateNetworkAsync(token);
            await CreateVolumeAsync(DataVolume, token);
        }
        if (id == "qbittorrent")
        {
            // Match WebUI/Host ports without disabling qBittorrent's Host validation.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        }
        var container = session + "-" + id;
        var volume = container + "-config";
        await CreateVolumeAsync(volume, token);
        resources.Add(("container", container));
        var args = new List<string> { "create", "--name", container, "--label", Label + "=" + session,
            "--network", Network, "--network-alias", id, "--publish", $"127.0.0.1:{(id == "qbittorrent" ? port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}:{port}",
            "--env", "PUID=1000", "--env", "PGID=1000",
            "--mount", $"type=volume,source={volume},target={(id == "seerr" ? "/app/config" : "/config")}",
            "--mount", $"type=volume,source={DataVolume},target={(id == "jellyfin" ? "/data/media,volume-subpath=media,readonly" : "/data")}",
            "--log-driver", "json-file", "--log-opt", "max-size=1m", "--log-opt", "max-file=1" };
        if (id == "qbittorrent") args.AddRange(["--env", "WEBUI_PORT=" + port]);
        if (id == "jellyfin") args.AddRange(["--tmpfs", "/cache:rw,uid=1000,gid=1000,mode=0755"]);
        args.Add(image);
        _ = await RunAsync(args, token);
        _ = await RunAsync(["start", container], token);
        services.Add(id, (container, await ReadBindingAsync(container, port, token), port));
        await WaitMediaAsync(id, token);
    }

    private async Task<Uri> ReadBindingAsync(string container, int port, CancellationToken token)
    {
        var binding = await RunAsync(["port", container, port + "/tcp"], token);
        if (!binding.Trim().StartsWith("127.0.0.1:", StringComparison.Ordinal) ||
            !int.TryParse(binding.Trim()[10..], out var hostPort)) throw new InvalidOperationException("bench.port.invalid");
        return new Uri($"http://127.0.0.1:{hostPort}/");
    }

    public async Task PrepareMediaFoldersAsync(CancellationToken token)
    {
        var container = services.TryGetValue("qbittorrent", out var qb) ? qb.Container : services["radarr"].Container;
        await VerifyOwnershipAsync("container", container, token);
        await VerifyOwnershipAsync("volume", DataVolume, token);
        // Fixed paths in our fresh named volume only. Never a recursive permission change.
        _ = await RunAsync(["exec", container, "chown", "1000:1000", "/data"], token);
        _ = await RunAsync(["exec", "--user", "1000:1000", container, "mkdir", "-p",
            "/data/media/movies", "/data/media/tv", "/data/torrents/movies", "/data/torrents/tv"], token);
    }

    public async Task<string> ReadMediaSecretAsync(string id, CancellationToken token)
    {
        var container = services[id].Container;
        await VerifyOwnershipAsync("container", container, token);
        if (id == "qbittorrent")
        {
            var logs = await RunAsync(["logs", container], token, includeError: true);
            var match = TemporaryPasswordPattern().Match(logs);
            if (!match.Success) throw new InvalidOperationException("bench.temporary-password.unavailable");
            return match.Groups[1].Value.Trim();
        }
        var config = await RunAsync(["exec", container, "cat", "/config/config.xml"], token);
        var xml = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(config), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        xml.Load(reader);
        var key = xml.SelectSingleNode("/Config/ApiKey")?.InnerText;
        if (key is null || key.Length != 32 || !key.All(char.IsAsciiHexDigit)) throw new InvalidOperationException("bench.api-key.unavailable");
        return key;
    }

    public async Task RestartMediaServiceAsync(string id, CancellationToken token)
    {
        var container = services[id].Container;
        await VerifyOwnershipAsync("container", container, token);
        _ = await RunAsync(["stop", "--time", "10", container], token);
        _ = await RunAsync(["start", container], token);
        var port = services[id].Port;
        services[id] = (container, await ReadBindingAsync(container, port, token), port);
        await WaitMediaAsync(id, token);
    }

    private async Task WaitMediaAsync(string id, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        using var http = new HttpClient(handler) { BaseAddress = services[id].Endpoint, Timeout = TimeSpan.FromSeconds(3) };
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                var path = id switch { "qbittorrent" => "api/v2/app/version", "jellyfin" => "System/Info/Public", "seerr" => "api/v1/status", _ => "ping" };
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Referrer = http.BaseAddress;
                using var reply = await http.SendAsync(request, deadline.Token);
                if (reply.StatusCode == (id == "qbittorrent" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)) return;
                if (id == "qbittorrent" && reply.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("bench.qb.authentication-policy.invalid");
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(500, deadline.Token);
        }
    }
}
