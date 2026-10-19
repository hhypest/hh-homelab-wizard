# hh-homelab-wizard:nas-paths:v1
set -eu
LC_ALL=C
export LC_ALL
unset CDPATH
# Paths arrive only on stdin and are always quoted; nothing from the request is evaluated.

fail() {
    exit 2
}

unavailable() {
    printf 'entry=unavailable:0:unavailable:-:-:-:-:unavailable\n'
}

contents_of() {
    if [ ! -r "$1" ] || [ ! -x "$1" ]; then
        printf 'unavailable'
    elif ! ls -A "$1" >/dev/null 2>&1; then
        printf 'unavailable'
    elif [ -n "$(ls -A "$1" | head -n 1)" ]; then
        printf 'not-empty'
    else
        printf 'empty'
    fi
}

canonical_of() {
    if [ -L "$1" ]; then
        printf 'redirected'
        return
    fi
    parent="$(dirname "$1")"
    if ! resolved="$(cd -P "$parent" 2>/dev/null && pwd -P)"; then
        printf 'unavailable'
    elif [ "$resolved" = "$parent" ]; then
        printf 'same'
    else
        printf 'redirected'
    fi
}

inspect() {
    probe="$1"
    depth=0
    while [ ! -e "$probe" ] && [ ! -L "$probe" ]; do
        probe="$(dirname "$probe")"
        depth=$((depth + 1))
    done
    # A child of an unsearchable directory cannot be told apart from a missing one.
    if [ "$depth" -gt 0 ] && [ -d "$probe" ] && [ ! -x "$probe" ]; then
        unavailable
        return
    fi
    if ! facts="$(stat -c '%d:%u:%g:%a' "$probe" 2>/dev/null)"; then
        unavailable
        return
    fi
    canonical="$(canonical_of "$probe")"
    if [ ! -d "$probe" ]; then
        printf 'entry=not-directory:%s:%s:%s:not-applicable\n' "$depth" "$canonical" "$facts"
    elif [ "$depth" -gt 0 ]; then
        printf 'entry=missing:%s:%s:%s:not-applicable\n' "$depth" "$canonical" "$facts"
    else
        printf 'entry=directory:0:%s:%s:%s\n' "$canonical" "$facts" "$(contents_of "$probe")"
    fi
}

IFS= read -r header || fail
[ "$header" = 'hh-homelab-wizard:nas-paths-request:v1' ] || fail
printf 'hh-homelab-wizard:nas-paths:v1\n'
count=0
while IFS= read -r line; do
    if [ "$line" = end ]; then
        printf 'end\n'
        exit 0
    fi
    case "$line" in
        path=/*) ;;
        *) fail ;;
    esac
    count=$((count + 1))
    [ "$count" -le 8 ] || fail
    inspect "${line#path=}"
done
fail
