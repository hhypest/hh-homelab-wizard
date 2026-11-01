# hh-homelab-wizard:nas-compose:v1
set -eu
LC_ALL=C
export LC_ALL
unset DOCKER_HOST DOCKER_CONTEXT DOCKER_TLS_VERIFY DOCKER_CERT_PATH DOCKER_API_VERSION DOCKER_CUSTOM_HEADERS

fail() {
    exit 2
}

read_field() {
    IFS= read -r line || fail
    case "$line" in "$1="*) ;; *) fail ;; esac
    printf '%s' "${line#"$1="}"
}

docker_name() {
    case "$1" in ''|*[!a-zA-Z0-9_.-]*|[!a-zA-Z0-9]*) fail ;; esac
    [ "${#1}" -le 63 ] || fail
}

IFS= read -r header || fail
[ "$header" = 'hh-homelab-wizard:nas-compose-request:v1' ] || fail
project="$(read_field project)"
network_key="$(read_field network-key)"
network="$(read_field network)"
service="$(read_field service)"
container="$(read_field container)"
directory="$(read_field directory)"
IFS= read -r terminator || fail
[ "$terminator" = end ] || fail

case "$project" in hhw-*) ;; *) fail ;; esac
installation="${project#hhw-}"
case "$installation" in *[!0-9a-f]*) fail ;; esac
[ "${#installation}" -eq 32 ] || fail
docker_name "$network_key"
docker_name "$network"
case "$network" in *.*) fail ;; esac
docker_name "$service"
docker_name "$container"
case "$directory" in /volume[0-9]*/*) ;; *) fail ;; esac
[ "${#directory}" -le 240 ] || fail

# Everything is collected first and printed only when every Docker call succeeded: a failure after
# partial output must never look like a smaller, complete answer.
unavailable() {
    printf 'hh-homelab-wizard:nas-compose:v1\nstatus=unavailable\nend\n'
    exit 0
}

docker_binary="$(command -v docker || true)"
[ -n "$docker_binary" ] || unavailable

docker_local() {
    "$docker_binary" --host unix:///var/run/docker.sock "$@" 2>/dev/null
}

# Prints one label value followed by a dot, so a trailing newline in a foreign value cannot vanish in
# command substitution and make it equal to the expected value. Template keys are fixed constants.
label_of() {
    docker_local "$1" inspect --format "{{index $2 \"$3\"}}." "$4"
}

same() {
    [ "$1" = "$2." ]
}

network_ids="$(docker_local network ls --no-trunc --filter "name=$network" \
    --format "{{if eq .Name \"$network\"}}{{.ID}}{{end}}")" || unavailable
container_ids="$(docker_local container ls --all --no-trunc --filter "name=$container" \
    --format "{{if eq .Names \"$container\"}}{{.ID}}{{end}}")" || unavailable

result=''
count=0
for id in $network_ids; do
    count=$((count + 1))
    [ "$count" -le 16 ] || unavailable
    case "${#id}" in
        64) case "$id" in *[!0-9a-f]*) unavailable ;; esac ;;
        25) case "$id" in *[!0-9a-z]*) unavailable ;; esac ;;
        *) unavailable ;;
    esac
    owner="$(label_of network .Labels com.docker.compose.project "$id")" || unavailable
    key="$(label_of network .Labels com.docker.compose.network "$id")" || unavailable
    ownership=foreign
    if same "$owner" "$project" && same "$key" "$network_key"; then
        ownership=own
    fi
    result="${result}network=$id:$ownership
"
done

count=0
for id in $container_ids; do
    count=$((count + 1))
    [ "$count" -le 16 ] || unavailable
    [ "${#id}" -eq 64 ] || unavailable
    case "$id" in *[!0-9a-f]*) unavailable ;; esac
    owner="$(label_of container .Config.Labels com.docker.compose.project "$id")" || unavailable
    named="$(label_of container .Config.Labels com.docker.compose.service "$id")" || unavailable
    oneoff="$(label_of container .Config.Labels com.docker.compose.oneoff "$id")" || unavailable
    workdir="$(label_of container .Config.Labels com.docker.compose.project.working_dir "$id")" || unavailable
    state="$(docker_local container inspect --format '{{.State.Status}}' "$id")" || unavailable
    case "$state" in created|running|paused|restarting|removing|exited|dead) ;; *) unavailable ;; esac
    ownership=foreign
    if same "$owner" "$project" && same "$named" "$service" && same "$oneoff" False && same "$workdir" "$directory"; then
        ownership=own
    fi
    result="${result}container=$id:$ownership:$state
"
done

printf 'hh-homelab-wizard:nas-compose:v1\n%sstatus=available\nend\n' "$result"
