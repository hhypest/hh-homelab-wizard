#!/bin/sh
set -eu
case "${1:-}" in
    start)
        # No daemon from the host, bridge, iptables, TCP listener or image pull.
        test ! -e /run/wizard-docker.pid
        dockerd --host unix:///var/run/docker.sock --group docker \
            --pidfile /run/wizard-docker.pid --data-root /var/lib/wizard-docker \
            --exec-root /run/wizard-docker --storage-driver vfs \
            --feature containerd-snapshotter=false \
            --bridge none --iptables=false --ip6tables=false --ip-forward=false --ip-masq=false \
            > /run/wizard-docker.log 2>&1 &
        ;;
    stop)
        pid="$(cat /run/wizard-docker.pid)"
        case "$pid" in ''|*[!0-9]*) exit 1 ;; esac
        test "$(cat "/proc/$pid/comm")" = dockerd
        kill -TERM "$pid"
        for attempt in $(seq 1 100); do
            if [ ! -e "/proc/$pid/comm" ]; then exit 0; fi
            if grep -q '^State:.*Z' "/proc/$pid/status"; then exit 0; fi
            sleep 0.1
        done
        exit 1
        ;;
    *) exit 2 ;;
esac
