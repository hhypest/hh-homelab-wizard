# hh-homelab-wizard:nas-directory-create:v1
set -eu
LC_ALL=C
export LC_ALL
unset CDPATH
umask 022
# Creates one configuration directory (and its missing parents) below this installation's own marked
# root. Root, marker, relative path and mode arrive only on stdin. Exit 3 means nothing changed; exit 0
# created or found the directory with exactly this mode; anything else may have changed the NAS and must
# be observed, not retried blindly. Existing directories are never re-moded, re-owned or emptied.

framing() {
    exit 2
}

reject() {
    exit 3
}

same_marker_bytes() {
    [ -f "$1" ] && [ ! -L "$1" ] && [ -r "$1" ] || return 1
    size_of_marker="$(stat -c '%s' "$1" 2>/dev/null)" || return 1
    [ $((size_of_marker)) -le 4096 ] || return 1
    [ "$(od -An -v -tx1 "$1" | tr -d ' \n')" = "$(printf '%s' "$2" | od -An -v -tx1 | tr -d ' \n')" ]
}

IFS= read -r header || framing
[ "$header" = 'hh-homelab-wizard:nas-directory-create-request:v1' ] || framing
IFS= read -r root_line || framing
IFS= read -r marker_line || framing
IFS= read -r path_line || framing
IFS= read -r mode_line || framing
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
case "$path_line" in
    path=?*) path="${path_line#path=}" ;;
    *) framing ;;
esac
case "$path" in
    /* | */ | *//* | .hh-homelab-wizard | .hh-homelab-wizard/* | *[!A-Za-z0-9._/-]*) framing ;;
esac
# No dot, dot-dot or dash-leading component: a name is never read as an option by mkdir.
case "/$path/" in
    */./* | */../* | */-*) framing ;;
esac
case "$mode_line" in
    mode=700 | mode=750 | mode=755) mode="${mode_line#mode=}" ;;
    *) framing ;;
esac

# Permission bits only: a setgid bit inherited from a parent share is kept and does not count.
same_directory() {
    actual="$(stat -c '%a' . 2>/dev/null)" || return 1
    [ "${actual#"${actual%???}"}" = "$mode" ] || return 1
    [ "$(stat -c '%u' . 2>/dev/null)" = "$(id -u)" ]
}

# Only below this installation's own marker, reached without any symlink or redirected ancestor.
dir="$root/.hh-homelab-wizard"
owned_root() {
    for candidate in "$root" "$dir" "$dir/installation.json"; do
        if [ -L "$candidate" ]; then
            return 1
        fi
    done
    [ -d "$root" ] || return 1
    [ "$(cd -P "$root" 2>/dev/null && pwd -P)" = "$root" ] || return 1
    same_marker_bytes "$dir/installation.json" "$marker"
}
owned_root || reject

# Walk relative to directories entered one at a time and verified by their resolved path, so a component
# swapped for a symlink can never redirect a new directory outside the root.
expected="$root"
cd -P "$root" 2>/dev/null || reject
[ "$(pwd -P)" = "$expected" ] || reject
remaining="$path"
while [ -n "$remaining" ]; do
    case "$remaining" in
        */*) part="${remaining%%/*}" ;;
        *) part="$remaining" ;;
    esac
    if [ -L "$part" ]; then
        reject
    fi
    if [ ! -e "$part" ]; then
        break
    fi
    [ -d "$part" ] && [ -x "$part" ] || reject
    cd -P "$part" 2>/dev/null || reject
    expected="$expected/$part"
    [ "$(pwd -P)" = "$expected" ] || reject
    case "$remaining" in
        */*) remaining="${remaining#*/}" ;;
        *) remaining='' ;;
    esac
done
if [ -z "$remaining" ]; then
    # The whole path exists: only this user's directory with the identical mode is accepted, never changed.
    if same_directory; then
        printf 'directory=already-present\n'
        exit 0
    fi
    reject
fi
[ -w . ] || reject
# Ownership is checked again just before the first change.
owned_root || reject

# Changes start here. Missing parents get the default 755; the requested directory is created with its
# mode at once (mkdir -m), so it never exists with a wider one.
while [ -n "$remaining" ]; do
    case "$remaining" in
        */*)
            part="${remaining%%/*}"
            remaining="${remaining#*/}"
            mkdir "$part"
            ;;
        *)
            part="$remaining"
            remaining=''
            mkdir -m "$mode" "$part"
            ;;
    esac
    expected="$expected/$part"
    cd -P "$part"
    [ "$(pwd -P)" = "$expected" ] || exit 1
done
same_directory || exit 1
owned_root || exit 1
printf 'directory=created\n'
