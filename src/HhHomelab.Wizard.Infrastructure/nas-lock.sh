# hh-homelab-wizard:nas-lock:v1
set -eu
LC_ALL=C
export LC_ALL
unset CDPATH
umask 022
# Installation lock below this wizard's own ownership marker. Root, expected marker and session arrive
# only on stdin. Exit 3 is a refusal before any change; exit 0 is the reported success; anything else
# may have changed the lock and must be inspected, never assumed.

framing() {
    exit 2
}

refuse() {
    printf 'lock=%s\n' "$1"
    exit 3
}

# Only a lowercase GUID is ever echoed back; any other holder content is reported as unknown. The
# file must be exactly 36 bytes, so a trailing newline or NUL (dropped by command substitution)
# cannot make a malformed holder look like this session's lock.
holder_of() {
    if [ ! -f "$1" ] || [ -L "$1" ] || [ ! -r "$1" ]; then
        printf 'unknown'
        return
    fi
    if ! size="$(stat -c '%s' "$1" 2>/dev/null)" || [ $((size)) -ne 36 ]; then
        printf 'unknown'
        return
    fi
    value="$(head -c 36 "$1")"
    case "$value" in
        ????????-????-????-????-????????????)
            case "$value" in
                *[!0-9a-f-]*) printf 'unknown' ;;
                *) printf '%s' "$value" ;;
            esac
            ;;
        *) printf 'unknown' ;;
    esac
}

age_of() {
    if modified="$(stat -c '%Y' "$1" 2>/dev/null)"; then
        printf '%s' "$(( $(date -u +%s) - modified ))"
    else
        printf 'unknown'
    fi
}

# Exact byte comparison through the same hex encoder on both sides: shell variables would drop
# trailing newlines and NUL bytes. The file is size-checked first so a huge file is never read.
same_marker_bytes() {
    [ -f "$1" ] && [ ! -L "$1" ] && [ -r "$1" ] || return 1
    size="$(stat -c '%s' "$1" 2>/dev/null)" || return 1
    [ $((size)) -le 4096 ] || return 1
    [ "$(od -An -v -tx1 "$1" | tr -d ' \n')" = "$(printf '%s' "$2" | od -An -v -tx1 | tr -d ' \n')" ]
}

IFS= read -r header || framing
[ "$header" = 'hh-homelab-wizard:nas-lock-request:v1' ] || framing
IFS= read -r action_line || framing
IFS= read -r root_line || framing
IFS= read -r marker_line || framing
IFS= read -r session_line || framing
IFS= read -r end_line || framing
[ "$end_line" = end ] || framing
case "$action_line" in
    action=acquire | action=release) action="${action_line#action=}" ;;
    *) framing ;;
esac
case "$root_line" in
    root=/*) root="${root_line#root=}" ;;
    *) framing ;;
esac
case "$marker_line" in
    'marker={'*'}') marker="${marker_line#marker=}" ;;
    *) framing ;;
esac
case "$session_line" in
    session=????????-????-????-????-????????????) session="${session_line#session=}" ;;
    *) framing ;;
esac
case "$session" in
    *[!0-9a-f-]*) framing ;;
esac

dir="$root/.hh-homelab-wizard"
file="$dir/installation.json"
lock="$dir/lock"
holder="$lock/holder"
for candidate in "$root" "$dir" "$file" "$lock" "$holder"; do
    if [ -L "$candidate" ]; then
        refuse invalid
    fi
done
# The resolved root must equal its lexical path: a symlinked ancestor would move the lock elsewhere.
if [ ! -d "$root" ] || [ "$(cd -P "$root" 2>/dev/null && pwd -P)" != "$root" ]; then
    refuse invalid
fi
# The lock exists only inside this installation's own marker directory.
if ! same_marker_bytes "$file" "$marker"; then
    refuse not-owned
fi

if [ "$action" = release ]; then
    if [ ! -e "$lock" ]; then
        printf 'lock=not-held\n'
        exit 0
    fi
    [ -d "$lock" ] || refuse invalid
    if [ "$(holder_of "$holder")" != "$session" ]; then
        refuse "held:$(holder_of "$holder"):$(age_of "$lock")"
    fi
    rm -f "$holder" "$lock"/.holder.tmp-*
    rmdir "$lock"
    printf 'lock=released\n'
    exit 0
fi

[ -w "$dir" ] && [ -x "$dir" ] || refuse invalid
if mkdir "$lock" 2>/dev/null; then
    tmp="$lock/.holder.tmp-$$"
    printf '%s' "$session" > "$tmp"
    mv -f "$tmp" "$holder"
    printf 'lock=acquired\n'
    exit 0
fi
[ -d "$lock" ] || refuse invalid
current="$(holder_of "$holder")"
if [ "$current" = "$session" ]; then
    printf 'lock=reacquired\n'
    exit 0
fi
refuse "held:$current:$(age_of "$lock")"
