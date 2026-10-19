#!/bin/sh
set -eu
identity_command="printf 'hh-homelab-wizard:ssh-probe:v1\\n'; uname -s; id -u"
preflight_command="$(cat /fixture/preflight.sh)"
paths_command="$(cat /fixture/paths.sh)"
marker_command="$(cat /fixture/marker-write.sh)"
mode="$(cat /fixture-state/mode 2>/dev/null || printf normal)"
case "${SSH_ORIGINAL_COMMAND:-}" in
    "$preflight_command")
        : > /fixture-state/identity-started
        case "$mode" in
            delay) exec sleep 60 ;;
            oversized) head -c 20000 /dev/zero | tr '\000' x; exit 0 ;;
            malformed) printf 'invalid-preflight\n'; exit 0 ;;
            docker-overrides)
                export DOCKER_HOST=tcp://127.0.0.1:1 DOCKER_CONTEXT=fixture-missing
                export DOCKER_TLS_VERIFY=1 DOCKER_CERT_PATH=/fixture-missing DOCKER_API_VERSION=invalid
                export DOCKER_CUSTOM_HEADERS=Authorization=synthetic-not-a-secret
                ;;
        esac
        exec /bin/sh /fixture/preflight.sh
        ;;
    "$paths_command")
        : > /fixture-state/identity-started
        case "$mode" in
            delay) exec sleep 60 ;;
            malformed) printf 'invalid-paths\n'; exit 0 ;;
        esac
        # The request stays on stdin; the client text is compared, never evaluated.
        exec /bin/sh /fixture/paths.sh
        ;;
    "$marker_command")
        : > /fixture-state/identity-started
        if [ "$mode" = marker-lost-response ]; then
            # The write really happens; only its response never reaches the client.
            /bin/sh /fixture/marker-write.sh > /dev/null 2>&1 || true
            exec sleep 60
        fi
        exec /bin/sh /fixture/marker-write.sh
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
