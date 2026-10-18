#!/bin/sh
set -eu
umask 077
mkdir -p /run/sshd
ssh-keygen -q -t ed25519 -N '' -f /run/sshd/hostkey
exec /usr/sbin/sshd -D -e -f /fixture/sshd_config
