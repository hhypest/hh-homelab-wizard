# hh-homelab-wizard:nas-preflight:v2
set -eu
LC_ALL=C
export LC_ALL
# Explicit local socket; never inherit another Docker endpoint/context or an API override.
unset DOCKER_HOST DOCKER_CONTEXT DOCKER_TLS_VERIFY DOCKER_CERT_PATH DOCKER_API_VERSION DOCKER_CUSTOM_HEADERS

read_key() {
    awk -F= -v key="$2" '$1 == key { value = substr($0, index($0, "=") + 1); gsub(/^"|"$/, "", value); print value }' "$1"
}

file_fact() {
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

printf 'hh-homelab-wizard:nas-preflight:v2\n'
printf 'kernel=%s\n' "$(uname -s)"
printf 'architecture=%s\n' "$(uname -m)"
printf 'unix-time=%s\n' "$(date -u +%s)"
# DSM file layout is a candidate contract until the separate hardware smoke.
file_fact dsm /etc.defaults/VERSION productversion
file_fact container-manager /var/packages/ContainerManager/INFO version
file_fact model /etc.defaults/synoinfo.conf upnpmodelname

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

# TCP listeners of this session's network namespace; IPv6 table is optional when IPv6 is disabled.
listening_ports() {
    awk 'function hex(text,  i, value) { value = 0; for (i = 1; i <= length(text); i++) value = value * 16 + index("0123456789ABCDEF", toupper(substr(text, i, 1))) - 1; return value }
        FNR > 1 && $4 == "0A" { count = split($2, address, ":"); print hex(address[count]) }' "$@" | sort -n -u
}

if [ ! -r /proc/net/tcp ] || { [ -e /proc/net/tcp6 ] && [ ! -r /proc/net/tcp6 ]; }; then
    printf 'listening=unavailable\n'
else
    tables=/proc/net/tcp
    if [ -e /proc/net/tcp6 ]; then
        tables="$tables /proc/net/tcp6"
    fi
    # shellcheck disable=SC2086 # fixed table paths without whitespace
    ports="$(listening_ports $tables)"
    printf 'listening=available\n'
    if [ -n "$ports" ]; then
        printf '%s\n' "$ports" | head -n 257 | while IFS= read -r port; do
            printf 'listen=%s\n' "$port"
        done
    fi
fi

# Capacity of DSM data volumes only; other mounts are not reported.
if volumes="$(df -P -k 2>/dev/null)"; then
    printf 'volumes=available\n'
    printf '%s\n' "$volumes" | awk 'NR > 1 && $6 ~ /^\/volume[1-9][0-9]*$/ { print "volume=" $6 ":" $2 ":" $4 }' | head -n 17
else
    printf 'volumes=unavailable\n'
fi
printf 'end\n'
