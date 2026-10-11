"""Developer fixture in the pinned HA runtime. No device actions or installed fake sensors."""
import asyncio
import importlib
from importlib.metadata import version
import json
import logging
from pathlib import Path
import sys

logging.disable(logging.CRITICAL)
sys.path.insert(0, "/config")
from annotatedyaml.loader import Secrets
from homeassistant.config import load_yaml_config_file
from homeassistant.core import HomeAssistant
from homeassistant.helpers import check_config
from homeassistant.helpers.template import Template


async def main():
    stages = ("yaml", "configuration", "monitor_import", "templates")
    result = dict.fromkeys(stages, "failed")
    hass = HomeAssistant("/config")
    try:
        config = await hass.async_add_executor_job(
            load_yaml_config_file, "/config/configuration.yaml", Secrets(Path("/config"))
        )
        packages = config["homeassistant"]["packages"]
        assert set(packages) == {"docker", "monitoring", "media_tv", "local_notifications"}
        assert set(importlib.import_module("annotatedyaml").loader.load_yaml(
            "/config/secrets.yaml")) == {"home_latitude", "home_longitude", "home_elevation",
                                         "tv_lg_mac", "jellyfin_user_id", "jellyfin_auth_header"}
        assert "pachca_webhook_url" not in Path("/config/secrets.yaml").read_text()
        result["yaml"] = "passed"
        importlib.import_module("homeassistant.loader").async_setup(hass)
        checked = await check_config.async_check_ha_config_file(hass)
        if not checked.errors and not checked.warnings:
            result["configuration"] = "passed"
        importlib.import_module("custom_components.monitor_docker.helpers")
        assert version("aiodocker") == "0.25.0"
        assert version("python-dateutil") == "2.9.0.post0"
        result["monitor_import"] = "passed"
        tv = packages["media_tv"]
        sensors = {s["unique_id"]: s for group in tv["template"] for s in group.get("binary_sensor", [])}
        watching = sensors["tv_watching"]
        idle = sensors["tv_idle_20min"]
        cases = [
            ("on", "org.jellyfin.androidtv", "idle", "playing", True, True),
            ("on", "org.jellyfin.androidtv", "idle", "buffering", True, True),
            ("on", "org.jellyfin.androidtv", "idle", "paused", True, False),
            ("on", "com.google.android.youtube.tv", "playing", "unavailable", True, True),
            ("on", "com.google.android.youtube.tv", "paused", "unavailable", True, False),
            ("on", "org.jellyfin.androidtv", "idle", "unavailable", False, False),
            ("on", "com.google.android.youtube.tv", "unavailable", "unavailable", False, False),
            ("unavailable", "org.jellyfin.androidtv", "playing", "playing", False, True),
            ("unknown", "org.jellyfin.androidtv", "idle", "unknown", False, False),
            ("off", "org.jellyfin.androidtv", "idle", "unavailable", True, False),
        ]
        for lg, app, cast, jellyfin, available, playing in cases:
            hass.states.async_set("media_player.lg_tv", lg)
            hass.states.async_set("media_player.rocktek_gx1", "on", {"app_id": app})
            hass.states.async_set("media_player.rocktek_gx1_cast", cast)
            hass.states.async_set("media_player.jellyfin_rocktek_gx1", jellyfin)
            assert Template(watching["availability"], hass).async_render() == available
            assert Template(watching["state"], hass).async_render() == playing
            hass.states.async_set("binary_sensor.tv_watching", "on" if playing else "off" if available else "unavailable")
            assert Template(idle["state"], hass).async_render() == (lg == "on" and available and not playing)
        for automation in tv["automation"]:
            if automation["id"] in {"tv_night_idle_off", "tv_night_sleep_check"}:
                assert any(c.get("entity_id") == "input_boolean.tv_no_auto_off" and c.get("state") == "off"
                           for c in automation["conditions"])
        result["template_cases"] = len(cases)
        result["templates"] = "passed"
    except Exception as error:
        # Never print exception text or configuration: either may contain secrets.
        result["error_kind"] = type(error).__name__ if type(error).__name__ in {
            "TypeError", "AttributeError", "KeyError", "AssertionError", "ImportError", "ModuleNotFoundError"
        } else "other"
    finally:
        print(json.dumps(result), flush=True)
        await hass.async_stop()
    return 0 if all(result[s] == "passed" for s in stages) else 1


sys.exit(asyncio.run(main()))
