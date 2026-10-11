"""Execute copied pinned sequences in HA's real Script engine, with fake device services.

Only delays/timeouts are accelerated in the copy. The server's /config is never
written. States/services live in a separate HomeAssistant object, not in the server.
This proves control flow, not real devices, wall-clock timing or scheduled triggers.
"""
import asyncio
from copy import deepcopy
import json
import logging
from pathlib import Path
import sys
from annotatedyaml.loader import Secrets
from homeassistant.config import load_yaml_config_file
from homeassistant.core import Context, HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant import loader
from homeassistant.helpers import condition, config_validation as cv, trigger
from homeassistant.helpers.script import Script, async_validate_actions_config

logging.disable(logging.CRITICAL)


def accelerated(value):
    if isinstance(value, list):
        return [accelerated(v) for v in value]
    if isinstance(value, dict):
        return {k: (0.03 if k == "delay" else 0.3) if k in {"delay", "timeout"} and isinstance(v, str)
                else accelerated(v) for k, v in value.items()}
    return value


async def run_case(tv, name):
    hass = HomeAssistant("/tmp/wizard-action-" + name)
    loader.async_setup(hass)
    await condition.async_setup(hass)
    await trigger.async_setup(hass)
    calls = []
    timers = []
    stops = []
    night_script = None
    scripts = {}
    hass.states.async_set("media_player.lg_tv", "on")
    hass.states.async_set("media_player.rocktek_gx1", "on", {"app_id": "org.jellyfin.androidtv"})
    hass.states.async_set("media_player.jellyfin_rocktek_gx1", "idle")
    hass.states.async_set("binary_sensor.tv_watching", "on")
    hass.states.async_set("input_boolean.tv_no_auto_off", "off")
    hass.states.async_set("sensor.jellyfin_resume", "synthetic", {"Id": "synthetic-item", "Type": "Movie",
                                                               "UserData": {"PlaybackPositionTicks": 125000000}})

    async def device(call):
        key = call.domain + "." + call.service
        calls.append((key, dict(call.data)))
        if key == "webostv.command":
            if name == "protect-warning":
                hass.states.async_set("input_boolean.tv_no_auto_off", "on")
            if name == "ended-warning":
                hass.states.async_set("binary_sensor.tv_watching", "off")
            if name == "unavailable-warning":
                hass.states.async_set("binary_sensor.tv_watching", "unavailable")
            if name == "cancel-warning":
                stops.append(asyncio.create_task(night_script.async_stop()))
            if name == "toast-error":
                raise HomeAssistantError("synthetic service error")
        if key == "media_player.media_pause":
            if name == "pause-error":
                raise HomeAssistantError("synthetic service error")
            if name not in {"pause-ignored", "source-lost"}:
                hass.states.async_set("binary_sensor.tv_watching", "off")
            if name == "source-lost":
                hass.states.async_set("binary_sensor.tv_watching", "unavailable")
            if name == "resumed":
                timers.append(hass.loop.call_later(0.1, hass.states.async_set, "binary_sensor.tv_watching", "on"))
            if name == "protect-grace":
                timers.append(hass.loop.call_later(0.1, hass.states.async_set, "input_boolean.tv_no_auto_off", "on"))
            if name == "cancel-grace":
                timers.append(hass.loop.call_later(0.1, lambda: stops.append(asyncio.create_task(night_script.async_stop()))))
        if key == "media_player.turn_off":
            for entity in call.data["entity_id"] if isinstance(call.data["entity_id"], list) else [call.data["entity_id"]]:
                hass.states.async_set(entity, "off")
        if key == "remote.turn_on" and name != "app-failed":
            hass.states.async_set("media_player.rocktek_gx1", "on", {"app_id": "org.jellyfin.androidtv"})
        if key == "media_player.play_media":
            hass.states.async_set("media_player.jellyfin_rocktek_gx1", "playing", {
                "media_content_id": "other-item" if name == "wrong-item" else call.data["media_content_id"]})

    for domain, names in {"webostv": ["command"], "media_player": ["media_pause", "turn_on", "turn_off", "play_media", "media_seek"],
                          "remote": ["turn_on"], "homeassistant": ["update_entity"], "persistent_notification": ["create"]}.items():
        for service in names:
            hass.services.async_register(domain, service, device)
    for script_id, config in tv["script"].items():
        sequence = await async_validate_actions_config(hass, cv.SCRIPT_SCHEMA(accelerated(deepcopy(config["sequence"]))))
        scripts[script_id] = Script(hass, sequence, script_id, "script", script_mode=config.get("mode", "single"))
        async def invoke(call, selected=script_id):
            await scripts[selected].async_run(context=call.context)
        hass.services.async_register("script", script_id, invoke)
    try:
        if name in {"confirmed", "resumed", "pause-ignored", "pause-error", "source-lost", "protect-warning", "protect-grace", "ended-warning", "toast-error", "manual-on", "cancel-warning", "cancel-grace", "unavailable-warning"}:
            night = next(a for a in tv["automation"] if a["id"] == "tv_night_sleep_check")
            if name == "manual-on":
                hass.states.async_set("input_boolean.tv_no_auto_off", "on")
            guards = await condition.async_conditions_from_config(hass,
                [cv.CONDITION_SCHEMA(deepcopy(c)) for c in night["conditions"]], logging.getLogger(__name__), "fixture")
            try:
                if guards.async_check():
                    sequence = await async_validate_actions_config(hass, cv.SCRIPT_SCHEMA(accelerated(deepcopy(night["actions"]))))
                    night_script = Script(hass, sequence, "night fixture", "automation")
                    await night_script.async_run(context=Context())
            finally:
                guards.async_unload()
            off = [data["entity_id"] for key, data in calls if key == "media_player.turn_off"]
            off = [ids[0] if isinstance(ids, list) else ids for ids in off]
            assert off == (["media_player.rocktek_gx1", "media_player.lg_tv"] if name in {"confirmed", "toast-error"} else [])
            if name in {"manual-on", "protect-warning", "ended-warning", "unavailable-warning", "cancel-warning"}:
                assert not any(key == "media_player.media_pause" for key, _ in calls)
            if name == "manual-on":
                assert calls == []
        else:
            if name == "missing-session":
                hass.states.async_set("media_player.jellyfin_rocktek_gx1", "unavailable")
            if name == "stale-resume":
                hass.states.async_set("sensor.jellyfin_resume", "unavailable", {"Id": "stale-item"})
            if name == "app-failed":
                hass.states.async_set("media_player.rocktek_gx1", "on", {"app_id": "other.app"})
            await scripts["tv_jellyfin_resume"].async_run(context=Context())
            play = [d for k, d in calls if k == "media_player.play_media"]
            seek = [d for k, d in calls if k == "media_player.media_seek"]
            assert len(play) == (1 if name in {"resume-ok", "wrong-item"} else 0)
            assert len(seek) == (1 if name == "resume-ok" else 0)
            if seek:
                assert seek[0]["seek_position"] == 12.5
            if name == "app-failed":
                assert sum(k == "remote.turn_on" for k, _ in calls) == 3
    finally:
        for timer in timers:
            timer.cancel()
        for script in scripts.values():
            await script.async_stop()
        if night_script is not None:
            await night_script.async_stop()
        if stops:
            await asyncio.gather(*stops)
        await hass.async_stop()


async def main():
    config = load_yaml_config_file("/config/configuration.yaml", Secrets(Path("/config")))
    tv = config["homeassistant"]["packages"]["media_tv"]
    cases = ("confirmed", "resumed", "pause-ignored", "pause-error", "source-lost", "protect-warning", "protect-grace",
             "ended-warning", "toast-error", "manual-on", "cancel-warning", "cancel-grace", "unavailable-warning",
             "resume-ok", "wrong-item", "missing-session", "stale-resume", "app-failed")
    result = {"status": "failed", "passed_cases": 0}
    for case in cases:
        try:
            await run_case(tv, case)
            result["passed_cases"] += 1
        except Exception as error:
            result["failed_case"] = case
            result["error_kind"] = type(error).__name__ if type(error).__name__ in {"TypeError", "AttributeError", "KeyError", "AssertionError", "HomeAssistantError"} else "other"
            break
    else:
        result["status"] = "passed"
    print(json.dumps(result), flush=True)
    return 0 if result["status"] == "passed" else 1


sys.exit(asyncio.run(main()))
