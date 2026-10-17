namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    public async Task VerifyDsmInteractiveFormsAsync(CancellationToken token)
    {
        await VerifyOwnershipAsync("container", Container, token);
        const string script = """
            import asyncio
            from types import SimpleNamespace
            import probatio
            from homeassistant.components.synology_dsm.config_flow import SynologyDSMFlowHandler
            from homeassistant.helpers import config_validation as cv
            async def verify():
                flow = SynologyDSMFlowHandler()
                otp = await flow.async_step_2sa({})
                fields = probatio.to_field_list(otp['data_schema'], custom_serializer=cv.custom_serializer)
                assert otp['step_id'] == '2sa' and [f['name'] for f in fields] == ['otp_code']
                try:
                    await flow.async_step_2sa(None)
                except AttributeError:
                    pass
                else:
                    raise AssertionError('pinned OTP GET contract changed')
                flow.hass = SimpleNamespace(config=SimpleNamespace(location_name='M0'))
                flow.shares = [SimpleNamespace(path='/volume1/backups', name='Synthetic backup')]
                backup = await flow.async_step_backup_share()
                fields = probatio.to_field_list(backup['data_schema'], custom_serializer=cv.custom_serializer)
                assert backup['step_id'] == 'backup_share' and {f['name'] for f in fields} == {'backup_share', 'backup_path'}
                share = next(f for f in fields if f['name'] == 'backup_share')
                assert share['selector']['select']['options'][0]['value'] == '/volume1/backups'
                print('synology-interactive-forms-passed')
            asyncio.run(verify())
            """;
        var result = await RunAsync(["exec", Container, "python3", "-c", script], token);
        if (result.Trim() != "synology-interactive-forms-passed")
            throw new InvalidOperationException("bench.ha.synology-forms.failed");
        Console.WriteLine(
            "ha: pinned DSM OTP/backup forms serialized with synthetic shares; unsafe OTP GET behavior verified; no DSM connection"
        );
    }

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
        if (result.Trim() != "synology-role-source-passed")
            throw new InvalidOperationException("bench.ha.synology-source.failed");
        Console.WriteLine("ha: pinned DSM metric/API keys verified; no DSM connection");
    }
}
