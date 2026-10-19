# hh-homelab-wizard:nas-network:v1
set -eu
LC_ALL=C
export LC_ALL
unset DOCKER_HOST DOCKER_CONTEXT DOCKER_TLS_VERIFY DOCKER_CERT_PATH DOCKER_API_VERSION DOCKER_CUSTOM_HEADERS

fail() {
    exit 2
}

IFS= read -r header || fail
[ "$header" = 'hh-homelab-wizard:nas-network-request:v1' ] || fail
IFS= read -r line || fail
case "$line" in network=*) network="${line#network=}" ;; *) fail ;; esac
case "$network" in ''|*[!a-zA-Z0-9_-]*|[!a-zA-Z0-9]*) fail ;; esac
[ "${#network}" -le 63 ] || fail
IFS= read -r terminator || fail
[ "$terminator" = end ] || fail

printf 'hh-homelab-wizard:nas-network:v1\n'
docker_binary="$(command -v docker || true)"
if [ -z "$docker_binary" ]; then
    printf 'status=unavailable\n'
elif "$docker_binary" --host unix:///var/run/docker.sock network ls --no-trunc \
    --filter "name=$network" \
    --format "{{if eq .Name \"$network\"}}network={{.ID}}{{else}}skip{{end}}" 2>/dev/null; then
    # Docker's name filter can match substrings. Compare .Name explicitly; the validated name
    # cannot contain template/string syntax. Other networks emit only a fixed skip record.
    # Stream IDs directly into the bounded SSH output reader; retain the CLI's exit code.
    printf 'status=available\n'
else
    printf 'status=unavailable\n'
fi
printf 'end\n'
