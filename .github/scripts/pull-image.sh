#!/usr/bin/env bash
set -euo pipefail

# Only image acquisition is retried. Never wrap probes or installation writes.
if [[ $# -ne 1 || -z "$1" ]]; then
  echo "Usage: bash pull-image.sh <pinned-image>" >&2
  exit 2
fi

image="$1"
pull_log="$(mktemp)"
trap 'rm -f "$pull_log"' EXIT

for attempt in 1 2 3 4; do
  echo "Pulling $image (attempt $attempt/4)"
  if docker pull -- "$image" > "$pull_log" 2>&1; then
    cat "$pull_log"
    exit 0
  else
    pull_status=$?
  fi
  cat "$pull_log" >&2

  # A bad pin or denied access requires a fix, not another registry request.
  if grep -Eiq 'unauthorized|denied|manifest unknown|not found|invalid reference format|no matching manifest' "$pull_log"; then
    exit "$pull_status"
  fi
  if ! grep -Eiq 'toomanyrequests|too many requests|(^|[^0-9])429([^0-9]|$)|service unavailable|bad gateway|gateway timeout|internal server error|unexpected status.*5[0-9]{2}|connection reset|connection refused|TLS handshake timeout|i/o timeout|context deadline exceeded|temporary failure in name resolution|unexpected EOF' "$pull_log"; then
    exit "$pull_status"
  fi
  if [[ "$attempt" -eq 4 ]]; then
    exit "$pull_status"
  fi

  delay=$((15 * (2 ** (attempt - 1))))
  echo "Transient registry failure; retrying in ${delay}s" >&2
  sleep "$delay"
done
