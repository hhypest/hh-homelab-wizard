#!/bin/sh
set -eu
identity_command="printf 'hh-homelab-wizard:ssh-probe:v1\\n'; uname -s; id -u"
preflight_command="$(cat /fixture/preflight.sh)"
mode="$(cat /fixture-state/mode 2>/dev/null || printf normal)"
case "${SSH_ORIGINAL_COMMAND:-}" in
    "$preflight_command")
        : > /fixture-state/identity-started
        case "$mode" in
            delay) exec sleep 60 ;;
            oversized) head -c 20000 /dev/zero | tr '\000' x; exit 0 ;;
            malformed) printf 'invalid-preflight\n'; exit 0 ;;
        esac
        exec /bin/sh /fixture/preflight.sh
        ;;
    "$identity_command")
        : > /fixture-state/identity-started
        case "$mode" in
            delay) exec sleep 60 ;;
            oversized) head -c 20000 /dev/zero | tr '\000' x; exit 0 ;;
            malformed) printf 'invalid-identity\n'; exit 0 ;;
        esac
        printf 'hh-homelab-wizard:ssh-probe:v1\n'
        uname -s
        id -u
        ;;
    'sudo -n -l')
        if [ "$mode" = sudo-denied ]; then exit 1; fi
        exec sudo -n -l
        ;;
    internal-sftp)
        if [ "$mode" = sftp-denied ]; then exit 126; fi
        exec /usr/lib/ssh/sftp-server
        ;;
    *) exit 126 ;;
esac
