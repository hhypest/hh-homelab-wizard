"""Synthetic Docker API on an owned volume's Unix socket; never a real Docker daemon.

Only GET/HEAD are implemented. A separate Docker exec changes the fixed scenario
file. HTTP clients cannot mutate the fixture or reach the developer's Docker API.
"""
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler
import json
from pathlib import Path
import re
import socketserver
import time
from urllib.parse import urlsplit

NAMES = ("qbittorrent", "prowlarr", "radarr", "sonarr", "flaresolverr", "jellyfin", "seerr", "homeassistant", "dockerproxy")
ROOT = Path("/fixture")
START = datetime.now(timezone.utc) - timedelta(minutes=5)


def stamp(value):
    return value.isoformat().replace("+00:00", "Z")


def scenario():
    try:
        return (ROOT / "scenario").read_text().strip()
    except FileNotFoundError:
        return "running"


def state(name, mode):
    return "exited" if name == "radarr" and mode in {"exited", "oom"} else "restarting" if name == "radarr" and mode == "restarting" else "running"


def inspect(name, mode):
    value = state(name, mode)
    return {"Id": name, "Name": "/" + name, "Image": "sha256:" + "a" * 64,
            "Created": stamp(START), "Config": {"Image": "synthetic-fixture:1"},
            "HostConfig": {"NetworkMode": "bridge"}, "State": {
                "Status": value, "Running": value == "running", "Paused": False,
                "Restarting": value == "restarting", "OOMKilled": mode == "oom" and name == "radarr",
                "ExitCode": 137 if mode == "oom" else 0, "StartedAt": stamp(START),
                "FinishedAt": stamp(datetime.now(timezone.utc)), "Health": {"Status": "healthy"}}}


def stats():
    tick = time.monotonic_ns()
    return {"read": stamp(datetime.now(timezone.utc)),
            "cpu_stats": {"cpu_usage": {"total_usage": tick // 10, "percpu_usage": [tick // 20] * 2},
                          "system_cpu_usage": tick * 2, "online_cpus": 2},
            "memory_stats": {"usage": 64 * 1024 * 1024, "limit": 256 * 1024 * 1024,
                             "stats": {"inactive_file": 0}},
            "networks": {"eth0": {"rx_bytes": 100, "tx_bytes": 200}},
            "blkio_stats": {"io_service_bytes_recursive": []}}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def send_json(self, payload, status=200):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = re.sub(r"^/v[\d.]+", "", urlsplit(self.path).path)
        mode = scenario()
        if mode == "http-error":
            self.send_json({"message": "synthetic backend error"}, 503)
        elif path == "/version":
            self.send_json({"Version": "29.8.2", "ApiVersion": "1.51", "MinAPIVersion": "1.24", "Os": "linux"})
        elif path == "/_ping":
            self.send_response(200); self.send_header("Content-Length", "2"); self.end_headers(); self.wfile.write(b"OK")
        elif path == "/info":
            self.send_json({"ServerVersion": "29.8.2", "Containers": 9, "ContainersRunning": sum(state(n, mode) == "running" for n in NAMES),
                            "ContainersPaused": 0, "ContainersStopped": int(mode in {"exited", "oom"}),
                            "Images": 1, "MemTotal": 8 * 1024 ** 3, "NCPU": 2,
                            "OperatingSystem": "synthetic fixture", "OSType": "linux", "Architecture": "x86_64", "KernelVersion": "fixture"})
        elif path == "/containers/json":
            if mode == "malformed":
                self.send_json({"not": "a list"})
            else:
                self.send_json([{"Id": n, "Names": ["/" + n], "Image": "synthetic-fixture:1", "State": state(n, mode),
                                 "Status": "Exited (137) synthetic" if n == "radarr" and mode == "oom" else state(n, mode)} for n in NAMES])
        elif path == "/events":
            # An open event stream keeps the unmodified component alive. Changes are
            # observed by the fast command_line sensor; hourly Monitor polling is preserved.
            self.send_response(200); self.send_header("Content-Type", "application/json"); self.end_headers()
            try:
                while True:
                    self.wfile.write(b'{"Type":"network","Action":"fixture-heartbeat","Actor":{"ID":"fixture","Attributes":{}}}\n')
                    self.wfile.flush(); time.sleep(1)
            except (BrokenPipeError, ConnectionResetError):
                pass
        elif match := re.fullmatch(r"/containers/([a-z_]+)/json", path):
            self.send_json(inspect(match[1], mode), 200 if match[1] in NAMES else 404)
        elif re.fullmatch(r"/containers/[a-z_]+/stats", path):
            self.send_json(stats())
        elif path.startswith("/images"):
            self.send_json([])
        else:
            self.send_json({"message": "synthetic route missing"}, 404)


class Server(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True


with Server(str(ROOT / "docker.sock"), Handler) as server:
    (ROOT / "docker.sock").chmod(0o666)  # Owned synthetic socket only; HAProxy worker may use another UID.
    server.serve_forever()
