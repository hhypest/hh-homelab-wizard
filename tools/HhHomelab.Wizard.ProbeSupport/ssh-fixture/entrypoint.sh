#!/bin/sh
set -eu
umask 077
mkdir -p /run/sshd
# A killed container may retain its tmpfs until the engine finishes cleanup.
# Every fixture process start must issue a fresh identity even in that case.
rm -f /run/sshd/hostkey /run/sshd/hostkey.pub /run/sshd/sshd.pid
rm -f /fixture-state/mode /fixture-state/identity-started
ssh-keygen -q -t ed25519 -N '' -f /run/sshd/hostkey
exec /usr/sbin/sshd -D -e -f /fixture/sshd_config
