# hh-homelab-wizard:nas-file-read:v1
set -eu
LC_ALL=C
export LC_ALL
unset CDPATH
# Read-only: whether the root carries this installation's exact marker, and only then the state of one
# file below it. Root, marker and relative path arrive only on stdin and are always quoted; nothing is
# written, followed through a symlink or evaluated.

fail() {
    exit 2
}

# Exact byte comparison through the same hex encoder on both sides: shell variables would drop
# trailing newlines and NUL bytes. The file is size-checked first so a huge file is never read.
same_marker_bytes() {
    [ -f "$1" ] && [ ! -L "$1" ] && [ -r "$1" ] || return 1
    size="$(stat -c '%s' "$1" 2>/dev/null)" || return 1
    [ $((size)) -le 4096 ] || return 1
    [ "$(od -An -v -tx1 "$1" | tr -d ' \n')" = "$(printf '%s' "$2" | od -An -v -tx1 | tr -d ' \n')" ]
}

marker_state() {
    dir="$1/.hh-homelab-wizard"
    file="$dir/installation.json"
    for candidate in "$1" "$dir" "$file"; do
        if [ -L "$candidate" ]; then
            printf 'redirected'
            return
        fi
    done
    if [ ! -e "$1" ]; then
        printf 'absent'
        return
    fi
    if [ ! -d "$1" ]; then
        printf 'different'
        return
    fi
    if ! resolved="$(cd -P "$1" 2>/dev/null && pwd -P)"; then
        printf 'unavailable'
        return
    fi
    if [ "$resolved" != "$1" ]; then
        printf 'redirected'
        return
    fi
    if [ -d "$dir" ] && [ ! -x "$dir" ]; then
        printf 'unavailable'
        return
    fi
    if [ ! -e "$file" ]; then
        printf 'absent'
        return
    fi
    if [ ! -r "$file" ]; then
        printf 'unavailable'
        return
    fi
    if same_marker_bytes "$file" "$2"; then
        printf 'same'
    else
        printf 'different'
    fi
}

# Walks the relative path one directory at a time: each directory is entered and its resolved path
# compared, so a component swapped for a symlink mid-walk is reported, never followed. The file itself is
# opened once and its real path read back through /proc, binding size, mode and digest to that inode.
# A missing parent means the file is absent. Runs in a subshell; contents travel only as size, mode and
# SHA-256.
file_state() {
    expected="$1"
    if ! cd -P "$expected" 2>/dev/null; then
        printf 'unavailable'
        return
    fi
    if [ "$(pwd -P)" != "$expected" ]; then
        printf 'redirected'
        return
    fi
    remaining="$2"
    while :; do
        case "$remaining" in
            */*)
                part="${remaining%%/*}"
                remaining="${remaining#*/}"
                ;;
            *)
                part="$remaining"
                remaining=''
                ;;
        esac
        if [ -L "$part" ]; then
            printf 'redirected'
            return
        fi
        if [ ! -e "$part" ]; then
            printf 'absent'
            return
        fi
        if [ -n "$remaining" ]; then
            if [ ! -d "$part" ]; then
                printf 'not-regular'
                return
            fi
            if ! cd -P "$part" 2>/dev/null; then
                printf 'unavailable'
                return
            fi
            expected="$expected/$part"
            if [ "$(pwd -P)" != "$expected" ]; then
                printf 'redirected'
                return
            fi
            continue
        fi
        if [ ! -f "$part" ]; then
            printf 'not-regular'
            return
        fi
        if [ ! -r "$part" ]; then
            printf 'unavailable'
            return
        fi
        exec 3< "$part"
        if [ "$(readlink /proc/self/fd/3 2>/dev/null)" != "$expected/$part" ] || [ ! -f /proc/self/fd/3 ]; then
            printf 'redirected'
            return
        fi
        if ! size="$(stat -L -c '%s' /proc/self/fd/3 2>/dev/null)" || ! mode="$(stat -L -c '%a' /proc/self/fd/3 2>/dev/null)"; then
            printf 'unavailable'
            return
        fi
        if [ $((size)) -gt 65536 ]; then
            printf 'oversized'
            return
        fi
        if ! digest="$(sha256sum <&3)"; then
            printf 'unavailable'
            return
        fi
        # The same inode must still be reachable at the requested path after hashing: an unlinked or
        # renamed file stays readable through the descriptor but is no longer this configuration file.
        if [ "$(readlink /proc/self/fd/3 2>/dev/null)" != "$expected/$part" ] || [ ! "$part" -ef /proc/self/fd/3 ]; then
            printf 'redirected'
            return
        fi
        printf 'present:%s:%s:%s' "$size" "$mode" "${digest%% *}"
        return
    done
}

IFS= read -r header || fail
[ "$header" = 'hh-homelab-wizard:nas-file-read-request:v1' ] || fail
IFS= read -r root_line || fail
IFS= read -r marker_line || fail
IFS= read -r path_line || fail
IFS= read -r end_line || fail
[ "$end_line" = end ] || fail
case "$root_line" in
    root=/*) root="${root_line#root=}" ;;
    *) fail ;;
esac
case "$marker_line" in
    'marker={'*'}') marker="${marker_line#marker=}" ;;
    *) fail ;;
esac
case "$path_line" in
    path=?*) path="${path_line#path=}" ;;
    *) fail ;;
esac
# The client validated the path; this repeats the same rule so the script never walks outside the root.
case "$path" in
    /* | */ | *//* | .hh-homelab-wizard | .hh-homelab-wizard/* | *[!A-Za-z0-9._/-]*) fail ;;
esac
# No dot, dot-dot or dash-leading component: a name is never read as an option by a utility.
case "/$path/" in
    */./* | */../* | */-*) fail ;;
esac

state="$(marker_state "$root" "$marker")"
printf 'hh-homelab-wizard:nas-file:v1\n'
printf 'marker=%s\n' "$state"
if [ "$state" = same ]; then
    printf 'file=%s\n' "$(file_state "$root" "$path")"
else
    printf 'file=not-checked\n'
fi
printf 'end\n'
