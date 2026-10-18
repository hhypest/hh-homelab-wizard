#!/usr/bin/env bash
set -euo pipefail

fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
export PROFILE_PULL_TRACE="$fixture/trace"
export PROFILE_PULL_MODE=success
cat > "$fixture/dotnet" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" != run ]]; then exit 0; fi
if [[ "$PROFILE_PULL_MODE" == empty ]]; then exit 0; fi
echo 'registry.example/first:1.2.3'
if [[ "$PROFILE_PULL_MODE" == fail ]]; then exit 1; fi
echo 'registry.example/second:4.5.6'
MOCK
cat > "$fixture/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$PROFILE_PULL_TRACE"
MOCK
chmod +x "$fixture/dotnet" "$fixture/docker"
export PATH="$fixture:$PATH"

bash .github/scripts/pull-profile-images.sh qbittorrent jellyfin > /dev/null
printf '%s\n' 'pull -- registry.example/first:1.2.3' 'pull -- registry.example/second:4.5.6' > "$fixture/expected"
cmp "$PROFILE_PULL_TRACE" "$fixture/expected"
rm "$PROFILE_PULL_TRACE"

for mode in fail empty; do
  export PROFILE_PULL_MODE="$mode"
  if bash .github/scripts/pull-profile-images.sh qbittorrent > /dev/null 2>&1; then
    echo "Expected profile selection rejection: $mode" >&2
    exit 1
  fi
  test ! -e "$PROFILE_PULL_TRACE"
done
if bash .github/scripts/pull-profile-images.sh > /dev/null 2>&1; then
  echo 'Expected missing selection to fail' >&2
  exit 1
fi
test ! -e "$PROFILE_PULL_TRACE"
echo 'Profile selection/pull boundary passed (synthetic commands; no Docker access).'
