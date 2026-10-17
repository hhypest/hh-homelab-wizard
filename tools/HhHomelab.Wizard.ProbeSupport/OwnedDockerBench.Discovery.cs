namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    /// <summary>Pinned native handlers/serializer with synthetic discovery metadata. No network pairing,
    /// registry insertion or physical device emulation; separate Python process inside our container.</summary>
    public async Task VerifyDiscoveryContractsAsync(CancellationToken token)
    {
        await VerifyOwnershipAsync("container", Container, token);
        const string script = """
            import asyncio
            from types import SimpleNamespace
            from unittest.mock import Mock, patch
            from uuid import UUID
            import probatio
            from homeassistant.data_entry_flow import FlowManager
            from homeassistant.helpers import config_validation as cv
            from homeassistant.components.synology_dsm.config_flow import SynologyDSMFlowHandler
            from homeassistant.components.webostv.config_flow import FlowHandler as LgFlow
            from homeassistant.components.androidtv_remote.config_flow import AndroidTVRemoteConfigFlow
            from homeassistant.components.cast.config_flow import FlowHandler as CastFlow, onboarding
            from homeassistant.components.cast.media_player import CastMediaPlayerEntity
            from homeassistant.components.androidtv_remote.entity import AndroidTVRemoteBaseEntity
            async def verify():
                cases = [
                    (SynologyDSMFlowHandler(), 'synology_dsm', 'ssdp', 'link', 'AABBCCDDEEFF', {'username', 'password', 'port', 'ssl', 'verify_ssl'}),
                    (LgFlow(), 'webostv', 'ssdp', 'pairing', '00000000-0000-0000-0000-000000000001', set()),
                    (AndroidTVRemoteConfigFlow(), 'androidtv_remote', 'zeroconf', 'zeroconf_confirm', 'aa:bb:cc:dd:ee:ff', set()),
                    (CastFlow(), 'cast', 'zeroconf', 'confirm', 'cast', set())]
                for flow, domain, source, step, identity, names in cases:
                    flow.handler = domain
                    flow.flow_id = 'a' * 32
                    flow.context = {'source': source, 'unique_id': identity}
                    flow.hass = SimpleNamespace()
                    if domain == 'synology_dsm':
                        flow.discovered_conf = {'name': 'Synthetic NAS', 'host': '192.0.2.1'}
                    elif domain == 'webostv':
                        flow._async_abort_entries_match = Mock()
                    elif domain == 'androidtv_remote':
                        flow.name = 'Synthetic Android'
                    with patch.object(onboarding, 'async_is_onboarded', return_value=True):
                        result = await getattr(flow, 'async_step_' + step)(None)
                    assert result['type'] == 'form' and result['step_id'] == step
                    schema = result['data_schema']
                    fields = probatio.to_field_list(schema, custom_serializer=cv.custom_serializer) if schema is not None else []
                    assert {f['name'] for f in fields} == names
                    flow.cur_step = result
                    metadata = FlowManager._async_flow_handler_to_flow_result(SimpleNamespace(_flow_result=dict), [flow], False)
                    assert metadata == [{'flow_id': 'a' * 32, 'handler': domain, 'context': flow.context, 'step_id': step}]
                uuid = UUID('00000000-0000-0000-0000-000000000001')
                info = SimpleNamespace(uuid=uuid, friendly_name='Synthetic Cast', cast_info=SimpleNamespace(manufacturer='Synthetic', model_name='Fixture', cast_type='cast'))
                cast = CastMediaPlayerEntity(None, SimpleNamespace(), info)
                assert cast.unique_id == str(uuid) and cast.device_info['identifiers'] == {('cast', uuid.hex)}
                entry = SimpleNamespace(unique_id='aa:bb:cc:dd:ee:ff', options={}, data={'mac': 'AA:BB:CC:DD:EE:FF', 'name': 'Synthetic Android'})
                remote = AndroidTVRemoteBaseEntity(SimpleNamespace(is_on=False, device_info={'manufacturer': 'Synthetic', 'model': 'Fixture'}), entry)
                assert remote.device_info['identifiers'] == {('androidtv_remote', entry.unique_id)}
                print('native-discovery-contract-passed')
            asyncio.run(verify())
            """;
        var result = await RunAsync(["exec", Container, "python3", "-c", script], token);
        if (result.Trim() != "native-discovery-contract-passed")
            throw new InvalidOperationException("bench.ha.discovery-source.failed");
        Console.WriteLine(
            "ha: native discovery forms/progress serializer and Cast/Android identity constructors verified with synthetic metadata; no device registered or connected"
        );
    }
}
