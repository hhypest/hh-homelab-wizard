# hh-homelab-wizard:nas-preflight:v1
set -eu
LC_ALL=C
export LC_ALL
# Explicit local socket; never inherit another Docker endpoint/context or an API override.
unset DOCKER_HOST DOCKER_CONTEXT DOCKER_TLS_VERIFY DOCKER_CERT_PATH DOCKER_API_VERSION DOCKER_CUSTOM_HEADERS

read_key() {
    awk -F= -v key="$2" '$1 == key { value = substr($0, index($0, "=") + 1); gsub(/^"|"$/, "", value); print value }' "$1"
}

file_version() {
    field="$1"
    file="$2"
    if [ ! -e "$file" ]; then
        printf '%s=missing\n' "$field"
    elif [ ! -r "$file" ]; then
        printf '%s=unavailable\n' "$field"
    else
        version="$(read_key "$file" "$3")"
        if [ "$field" = dsm ]; then
            version="$version-$(read_key "$file" buildnumber)"
        fi
        printf '%s=available:%s\n' "$field" "$version"
    fi
}

printf 'hh-homelab-wizard:nas-preflight:v1\n'
printf 'kernel=%s\n' "$(uname -s)"
printf 'architecture=%s\n' "$(uname -m)"
printf 'unix-time=%s\n' "$(date -u +%s)"
# DSM file layout is a candidate contract until the separate hardware smoke.
file_version dsm /etc.defaults/VERSION productversion
file_version container-manager /var/packages/ContainerManager/INFO version

docker_binary="$(command -v docker || true)"
if [ -z "$docker_binary" ]; then
    printf 'docker=missing\ncompose=missing\ncontainers=unavailable\n'
else
    if version="$("$docker_binary" --host unix:///var/run/docker.sock version --format '{{.Server.Version}}' 2>/dev/null)"; then
        printf 'docker=available:%s\n' "$version"
    else
        printf 'docker=unavailable\n'
    fi
    if version="$("$docker_binary" --host unix:///var/run/docker.sock compose version --short 2>/dev/null)"; then
        printf 'compose=available:%s\n' "${version#v}"
    else
        printf 'compose=unavailable\n'
    fi
    if containers="$("$docker_binary" --host unix:///var/run/docker.sock container ls --all --last 129 --quiet --no-trunc 2>/dev/null)"; then
        printf 'containers=available\n'
        if [ -n "$containers" ]; then
            printf '%s\n' "$containers" | while IFS= read -r container; do
                printf 'container=%s\n' "$container"
            done
        fi
    else
        printf 'containers=unavailable\n'
    fi
fi
printf 'end\n'
