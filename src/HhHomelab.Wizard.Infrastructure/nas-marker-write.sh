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

# True when the listing has an entry other than the allowed name pattern (a newline-only name too).
has_other_entries() {
    [ $(( $(ls -A "$1" | grep -v -x -e "$2" | head -c 1 | wc -c) )) -gt 0 ]
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
[ -d "$parent" ] || reject
if [ -e "$file" ]; then
    [ -f "$file" ] || reject
    if [ "$(cat "$file")" = "$marker" ]; then
        printf 'marker=already-present\n'
        exit 0
    fi
    reject
fi
if [ -e "$root" ]; then
    [ -d "$root" ] || reject
    if has_other_entries "$root" '\.hh-homelab-wizard'; then
        reject
    fi
fi
if [ -e "$dir" ]; then
    [ -d "$dir" ] || reject
    if has_other_entries "$dir" '\.installation\.json\.tmp-[0-9][0-9]*'; then
        reject
    fi
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
tmp="$dir/.installation.json.tmp-$$"
printf '%s' "$marker" > "$tmp"
if ! ln "$tmp" "$file"; then
    rm -f "$tmp"
    exit 1
fi
rm -f "$tmp"
printf 'marker=created\n'
