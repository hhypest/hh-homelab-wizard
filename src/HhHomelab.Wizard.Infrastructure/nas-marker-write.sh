# hh-homelab-wizard:nas-marker-write:v1
set -eu
LC_ALL=C
export LC_ALL
unset CDPATH
umask 022
# Root and marker arrive only on stdin and are always quoted. Exit 3 means every precondition was
# checked before the first change; exit 0 created or found the identical marker; anything else may
# have changed the NAS and must be observed, not retried blindly.

framing() {
    exit 2
}

reject() {
    exit 3
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

# Only this wizard's own regular temporary files, either never written (empty) or holding exactly the
# requested marker; another installation's leftover, a symlink or anything else is foreign content.
staged_temporary_entry() {
    name="${1##*/}"
    case "$name" in
        .installation.json.tmp-*) ;;
        *) return 1 ;;
    esac
    case "${name#.installation.json.tmp-}" in
        '' | *[!0-9]*) return 1 ;;
    esac
    [ -f "$1" ] && [ ! -L "$1" ] || return 1
    [ ! -s "$1" ] || same_marker_bytes "$1" "$marker"
}

# Exact byte comparison through the same hex encoder on both sides: shell variables would drop
# trailing newlines and NUL bytes. The file is size-checked first so a huge file is never read.
same_marker_bytes() {
    [ -f "$1" ] && [ ! -L "$1" ] && [ -r "$1" ] || return 1
    size="$(stat -c '%s' "$1" 2>/dev/null)" || return 1
    [ $((size)) -le 4096 ] || return 1
    [ "$(od -An -v -tx1 "$1" | tr -d ' \n')" = "$(printf '%s' "$2" | od -An -v -tx1 | tr -d ' \n')" ]
}

# The resolved directory must equal its lexical path: a symlinked ancestor anywhere redirects writes.
canonical_directory() {
    [ -d "$1" ] && [ "$(cd -P "$1" 2>/dev/null && pwd -P)" = "$1" ]
}

IFS= read -r header || framing
[ "$header" = 'hh-homelab-wizard:nas-marker-request:v1' ] || framing
IFS= read -r root_line || framing
IFS= read -r marker_line || framing
IFS= read -r end_line || framing
[ "$end_line" = end ] || framing
case "$root_line" in
    root=/*) root="${root_line#root=}" ;;
    *) framing ;;
esac
case "$marker_line" in
    'marker={'*'}') marker="${marker_line#marker=}" ;;
    *) framing ;;
esac

dir="$root/.hh-homelab-wizard"
file="$dir/installation.json"
parent="$(dirname "$root")"
for candidate in "$parent" "$root" "$dir" "$file"; do
    if [ -L "$candidate" ]; then
        reject
    fi
done
canonical_directory "$parent" || reject
if [ -e "$file" ]; then
    if same_marker_bytes "$file" "$marker"; then
        printf 'marker=already-present\n'
        exit 0
    fi
    reject
fi
if [ -e "$root" ]; then
    [ -d "$root" ] && [ -r "$root" ] && [ -x "$root" ] || reject
    only_entries "$root" marker_directory_entry || reject
fi
if [ -e "$dir" ]; then
    [ -d "$dir" ] && [ -r "$dir" ] && [ -x "$dir" ] || reject
    only_entries "$dir" staged_temporary_entry || reject
fi

# Write permission is checked up front, so a refusal is proven before anything changes.
if [ ! -e "$root" ]; then
    [ -w "$parent" ] && [ -x "$parent" ] || reject
elif [ ! -e "$dir" ]; then
    [ -w "$root" ] && [ -x "$root" ] || reject
else
    [ -w "$dir" ] && [ -x "$dir" ] || reject
fi

# Changes start here: create the root and marker directory, then publish the marker with a
# no-clobber hard link so a concurrent writer can never replace an existing marker.
[ -d "$root" ] || mkdir "$root"
[ -d "$dir" ] || mkdir "$dir"
# A fresh name that does not exist yet, created exclusively (noclobber) so no existing path is followed.
counter=0
tmp="$dir/.installation.json.tmp-$$"
while [ -e "$tmp" ] || [ -L "$tmp" ]; do
    counter=$((counter + 1))
    tmp="$dir/.installation.json.tmp-$$0$counter"
done
(set -C; printf '%s' "$marker" > "$tmp")
if ! ln "$tmp" "$file"; then
    rm -f "$tmp"
    exit 1
fi
rm -f "$tmp"
printf 'marker=created\n'
