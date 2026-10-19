#!/bin/sh
set -eu
umask 077
mkdir -p /run/sshd
# Every process start issues a fresh identity and clears injected faults.
# Do not depend on the engine clearing these transient paths during restart.
rm -f /run/sshd/hostkey /run/sshd/hostkey.pub /run/sshd/sshd.pid
rm -f /fixture-state/mode /fixture-state/identity-started
ssh-keygen -q -t ed25519 -N '' -f /run/sshd/hostkey
if [ "${1:-}" = docker ]; then
    /fixture/docker-control.sh start
fi
exec /usr/sbin/sshd -D -e -f /fixture/sshd_config
