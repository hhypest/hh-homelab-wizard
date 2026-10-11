namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    public async Task VerifySynologyRoleSourceAsync(CancellationToken token)
    {
        await VerifyOwnershipAsync("container", Container, token);
        const string script = """
            from homeassistant.components.synology_dsm.sensor import INFORMATION_SENSORS, UTILISATION_SENSORS, STORAGE_VOL_SENSORS, STORAGE_DISK_SENSORS
            descriptions = {d.key: d for d in INFORMATION_SENSORS + UTILISATION_SENSORS + STORAGE_VOL_SENSORS + STORAGE_DISK_SENSORS}
            expected = {'temperature': 'SYNO.DSM.Info', 'cpu_total_load': 'SYNO.Core.System.Utilization',
                        'volume_percentage_used': 'SYNO.Storage.CGI.Storage', 'disk_status': 'SYNO.Storage.CGI.Storage'}
            for key, api in expected.items():
                assert descriptions[key].api_key == api and descriptions[key].translation_key == key
            print('synology-role-source-passed')
            """;
        var result = await RunAsync(["exec", Container, "python3", "-c", script], token);
        if (result.Trim() != "synology-role-source-passed") throw new InvalidOperationException("bench.ha.synology-source.failed");
        Console.WriteLine("ha: pinned DSM metric/API keys verified; no DSM connection");
    }
}
