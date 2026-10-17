using System.Security.Cryptography;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

if (args.Length != 1)
{
    Console.Error.WriteLine(
        "Usage: HaProfileProbe <hh-homelab-v1.3.0-checkout>; owned local fixture only."
    );
    return 2;
}
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
OwnedDockerBench? bench = null;
var exit = 1;
try
{
    var component = await MonitorDockerPackage.FetchAsync(cancel.Token);
    Console.WriteLine("monitor-docker: 39 pinned files verified, upstream license included");
    var settings = new HomeAssistantLocalSettings
    {
        NasAddress = "127.0.0.1",
        TvBroadcastAddress = "192.0.2.255",
        TvMac = "02:00:00:00:00:01",
        JellyfinUserId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
        JellyfinToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
        Latitude = 0,
        Longitude = 0,
    };
    var payload = await HomeAssistantLocalProfile.RenderAsync(
        args[0],
        settings,
        component,
        cancel.Token
    );
    Console.WriteLine(
        "ha-profile: verified local payload; synthetic credentials; no device integrations configured"
    );
    bench = await OwnedDockerBench.ConnectAsync(cancel.Token);
    await bench.CheckHomeAssistantProfileAsync(payload, cancel.Token);
    exit = 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("ha-profile: cancelled or deadline exceeded");
}
catch (Exception)
{
    Console.Error.WriteLine("ha-profile: failed; raw errors suppressed to protect configuration");
}
finally
{
    if (bench is not null)
    {
        await bench.DisposeAsync();
        if (!bench.CleanupSucceeded)
            exit = 1;
    }
}
return exit;
