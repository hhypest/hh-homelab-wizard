# hh-homelab-wizard:nas-paths:v3
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

# Read at most one full pathname and retain find's exit status. Even a newline-only basename
# keeps the nonempty directory prefix. No name is echoed or parsed; a failed listing is unknown.
contents_of() {
    if [ ! -r "$1" ] || [ ! -x "$1" ]; then
        printf 'unavailable'
    elif ! first="$(find "$1" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)"; then
        printf 'unavailable'
    elif [ -n "$first" ]; then
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

# Every entry must satisfy the named predicate. Names come from globbing, never from parsing
# newline-delimited ls output, so a name containing newlines stays a single entry.
only_entries() {
    for entry in "$1"/* "$1"/.[!.]* "$1"/..?*; do
        if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then
            continue
        fi
        "$2" "$entry" || return 1
    done
    return 0
}

marker_directory_entry() {
    [ "${1##*/}" = .hh-homelab-wizard ] && [ -d "$1" ] && [ ! -L "$1" ]
}

# Only this wizard's own regular temporary files; a symlink or anything else is foreign content.
staged_temporary_entry() {
    name="${1##*/}"
    case "$name" in
        .installation.json.tmp-*) ;;
        *) return 1 ;;
    esac
    case "${name#.installation.json.tmp-}" in
        '' | *[!0-9]*) return 1 ;;
    esac
    [ -f "$1" ] && [ ! -L "$1" ]
}

# Leftover of an interrupted marker creation: the root holds only the marker directory and that
# holds only this wizard's regular temporary files. Anything else stays an ordinary absent marker.
# Non-empty temporary files must hold identical bounded bytes; that content is reported so only the
# installation it names may complete the leftover. Empty files were never written and name nobody.
staged_of() {
    if [ ! -r "$1" ] || [ ! -r "$2" ]; then
        printf 'marker=unavailable\n'
        return
    elif ! only_entries "$1" marker_directory_entry; then
        printf 'marker=absent\n'
        return
    elif ! only_entries "$2" staged_temporary_entry; then
        printf 'marker=absent\n'
        return
    fi
    staged=''
    for entry in "$2"/.installation.json.tmp-*; do
        if [ ! -e "$entry" ]; then
            continue
        fi
        if [ ! -r "$entry" ] || ! size="$(stat -c '%s' "$entry" 2>/dev/null)"; then
            printf 'marker=unavailable\n'
            return
        fi
        if [ $((size)) -eq 0 ]; then
            continue
        fi
        if [ $((size)) -gt 4096 ]; then
            printf 'marker=invalid\n'
            return
        fi
        content="$(od -An -v -tx1 "$entry" | tr -d ' \n')"
        if [ -n "$staged" ] && [ "$content" != "$staged" ]; then
            printf 'marker=invalid\n'
            return
        fi
        staged="$content"
    done
    printf 'marker=staged\n'
    if [ -n "$staged" ]; then
        printf 'marker-content=%s\n' "$staged"
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
    # A root the SSH user cannot enter is unavailable, not redirected.
    if [ -d "$root" ]; then
        if ! resolved="$(cd -P "$root" 2>/dev/null && pwd -P)"; then
            printf 'marker=unavailable\n'
            return
        fi
        if [ "$resolved" != "$root" ]; then
            printf 'marker=redirected\n'
            return
        fi
    fi
    if [ ! -e "$file" ]; then
        if [ -d "$root" ] && [ ! -x "$root" ]; then
            printf 'marker=unavailable\n'
        elif [ -d "$dir" ] && [ ! -x "$dir" ]; then
            printf 'marker=unavailable\n'
        elif [ -e "$dir" ] && [ ! -d "$dir" ]; then
            printf 'marker=invalid\n'
        elif [ -d "$dir" ]; then
            staged_of "$root" "$dir"
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
[ "$header" = 'hh-homelab-wizard:nas-paths-request:v3' ] || fail
if ! root_device="$(stat -c '%d' / 2>/dev/null)"; then
    fail
fi
# Numeric process identity is independent of any directory owner, including a missing path's ancestor.
ssh_uid="$(id -u)" || fail
ssh_gid="$(id -g)" || fail
printf 'hh-homelab-wizard:nas-paths:v3\n'
# A path on the device of "/" sits in a leftover mount-point directory, not on a mounted volume.
printf 'root-device=%s\n' "$root_device"
printf 'identity=%s:%s\n' "$ssh_uid" "$ssh_gid"
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
