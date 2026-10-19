# hh-homelab-wizard:nas-paths:v2
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

# Counts bytes rather than capturing names: a name made only of newlines must not look empty.
contents_of() {
    if [ ! -r "$1" ] || [ ! -x "$1" ]; then
        printf 'unavailable'
    elif ! ls -A "$1" >/dev/null 2>&1; then
        printf 'unavailable'
    else
        bytes="$(ls -A "$1" | head -c 1 | wc -c)"
        if [ $((bytes)) -gt 0 ]; then
            printf 'not-empty'
        else
            printf 'empty'
        fi
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

# Ownership marker below the configuration root: symlinks anywhere on its path, a non-regular or
# oversized file and a hidden (unsearchable) location are reported, never followed or guessed.
marker_of() {
    root="$1"
    dir="$root/.hh-homelab-wizard"
    file="$dir/installation.json"
    for candidate in "$root" "$dir" "$file"; do
        if [ -L "$candidate" ]; then
            printf 'marker=redirected\n'
            return
        fi
    done
    # A symlinked ancestor redirects the whole root: never read a marker through it.
    if [ -d "$root" ] && [ "$(cd -P "$root" 2>/dev/null && pwd -P)" != "$root" ]; then
        printf 'marker=redirected\n'
        return
    fi
    if [ ! -e "$file" ]; then
        if [ -d "$root" ] && [ ! -x "$root" ]; then
            printf 'marker=unavailable\n'
        elif [ -d "$dir" ] && [ ! -x "$dir" ]; then
            printf 'marker=unavailable\n'
        elif [ -e "$dir" ] && [ ! -d "$dir" ]; then
            printf 'marker=invalid\n'
        else
            printf 'marker=absent\n'
        fi
        return
    fi
    if [ ! -f "$file" ]; then
        printf 'marker=invalid\n'
        return
    fi
    if [ ! -r "$file" ]; then
        printf 'marker=unavailable\n'
        return
    fi
    # Size from metadata, so an oversized file is rejected without reading it.
    if ! size="$(stat -c '%s' "$file" 2>/dev/null)"; then
        printf 'marker=unavailable\n'
        return
    fi
    if [ $((size)) -gt 4096 ]; then
        printf 'marker=invalid\n'
        return
    fi
    # Content travels as hex so no byte of the file can break the line protocol.
    printf 'marker=present\nmarker-content=%s\n' "$(od -An -v -tx1 "$file" | tr -d ' \n')"
}

IFS= read -r header || fail
[ "$header" = 'hh-homelab-wizard:nas-paths-request:v2' ] || fail
if ! root_device="$(stat -c '%d' / 2>/dev/null)"; then
    fail
fi
printf 'hh-homelab-wizard:nas-paths:v2\n'
# A path on the device of "/" sits in a leftover mount-point directory, not on a mounted volume.
printf 'root-device=%s\n' "$root_device"
count=0
owner_seen=0
while IFS= read -r line; do
    if [ "$line" = end ]; then
        [ "$owner_seen" -eq 1 ] || fail
        printf 'end\n'
        exit 0
    fi
    [ "$owner_seen" -eq 0 ] || fail
    case "$line" in
        path=/*)
            count=$((count + 1))
            [ "$count" -le 8 ] || fail
            inspect "${line#path=}"
            ;;
        owner=/*)
            owner_seen=1
            marker_of "${line#owner=}"
            ;;
        *) fail ;;
    esac
done
fail
