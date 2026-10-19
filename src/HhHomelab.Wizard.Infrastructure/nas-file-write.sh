# hh-homelab-wizard:nas-file-write:v1
set -eu
LC_ALL=C
export LC_ALL
unset CDPATH
umask 022
# Creates one configuration file below this installation's own marked root. Root, marker, relative
# path, mode, size and SHA-256 arrive as stdin lines, followed by exactly that many raw bytes. Exit 3
# means the target and its directories are unchanged; exit 0 created or found the identical file;
# anything else may have changed the NAS and must be observed, not retried blindly.

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

# Exactly the requested bytes and mode, by metadata and digest, never by shell variables.
same_file() {
    [ -f "$1" ] && [ ! -L "$1" ] && [ -r "$1" ] || return 1
    [ "$(stat -c '%s' "$1" 2>/dev/null)" = "$size" ] || return 1
    [ "$(stat -c '%a' "$1" 2>/dev/null)" = "$mode" ] || return 1
    digest="$(sha256sum < "$1")" || return 1
    [ "${digest%% *}" = "$sha256" ]
}

IFS= read -r header || framing
[ "$header" = 'hh-homelab-wizard:nas-file-write-request:v1' ] || framing
IFS= read -r root_line || framing
IFS= read -r marker_line || framing
IFS= read -r path_line || framing
IFS= read -r mode_line || framing
IFS= read -r size_line || framing
IFS= read -r sha_line || framing
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
# No dot, dot-dot or dash-leading component: a name is never read as an option by mkdir or ln.
case "/$path/" in
    */./* | */../* | */-*) framing ;;
esac
case "$mode_line" in
    mode=600 | mode=640 | mode=644) mode="${mode_line#mode=}" ;;
    *) framing ;;
esac
case "$size_line" in
    size=0 | size=[1-9] | size=[1-9][0-9] | size=[1-9][0-9][0-9] | size=[1-9][0-9][0-9][0-9] | size=[1-9][0-9][0-9][0-9][0-9])
        size="${size_line#size=}"
        ;;
    *) framing ;;
esac
[ "$size" -le 65536 ] || framing
case "$sha_line" in
    sha256=*) sha256="${sha_line#sha256=}" ;;
    *) framing ;;
esac
case "$sha256" in
    *[!0-9a-f]*) framing ;;
esac
[ "${#sha256}" -eq 64 ] || framing

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
[ -w "$dir" ] && [ -x "$dir" ] || reject

# Every existing parent must be a real searchable directory; missing parents are created only after
# the content was received and verified, so the deepest existing one must be writable.
current="$root"
remaining="$path"
missing=''
while :; do
    case "$remaining" in
        */*)
            part="${remaining%%/*}"
            remaining="${remaining#*/}"
            ;;
        *) break ;;
    esac
    next="$current/$part"
    if [ -L "$next" ]; then
        reject
    fi
    if [ ! -e "$next" ]; then
        missing="$part/$remaining"
        break
    fi
    [ -d "$next" ] && [ -x "$next" ] || reject
    current="$next"
done
target="$root/$path"
if [ -z "$missing" ]; then
    if [ -L "$target" ]; then
        reject
    fi
    if [ -e "$target" ]; then
        if same_file "$target"; then
            printf 'file=already-present\n'
            exit 0
        fi
        reject
    fi
fi
[ -w "$current" ] && [ -x "$current" ] || reject

# The content is received into a fresh private name inside the wizard's own marker directory, never
# next to the target, and verified there; a refusal or cut transfer leaves the target untouched.
counter=0
staging="$dir/.file-$$"
while [ -e "$staging" ] || [ -L "$staging" ]; do
    counter=$((counter + 1))
    staging="$dir/.file-$$0$counter"
done
trap 'rm -f "$staging"' EXIT
trap 'exit 1' HUP INT TERM PIPE
# Owner-only from creation: a secret is never readable by others while it is being received.
(umask 077; set -C; head -c "$size" > "$staging")
chmod "$mode" "$staging"
same_file "$staging" || reject

# Publication works relative to directories entered one at a time and verified by their resolved path,
# so a checked parent swapped for a symlink can never redirect the new directories or the file outside
# the root. The first check precedes any change.
expected="$current"
cd -P "$current" 2>/dev/null || reject
[ "$(pwd -P)" = "$expected" ] || reject
# Ownership is checked again just before the first change, not only before the transfer; a marker
# replaced during the transfer is a refusal. The remote installation lock keeps other wizards out.
owned_root || reject

# Changes start here: create the missing parents, then publish with a no-clobber hard link so a
# concurrent writer or a later manual file is never replaced.
while [ -n "$missing" ]; do
    case "$missing" in
        */*)
            part="${missing%%/*}"
            missing="${missing#*/}"
            mkdir "$part"
            expected="$expected/$part"
            cd -P "$part"
            [ "$(pwd -P)" = "$expected" ] || exit 1
            ;;
        *) missing='' ;;
    esac
done
# -T: an entry raced in under the final name, even a directory or a symlink to one, makes ln fail
# instead of receiving the link inside it.
name="${path##*/}"
ln -T "$staging" "$name"
# The published name must hold the verified bytes and the root must still be this installation's;
# otherwise the outcome is unknown and the next observation reports it.
same_file "$name" || exit 1
owned_root || exit 1
printf 'file=created\n'
