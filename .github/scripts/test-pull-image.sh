#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
export PULL_TEST_ROOT
PULL_TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$PULL_TEST_ROOT"' EXIT
mkdir "$PULL_TEST_ROOT/bin"

cat > "$PULL_TEST_ROOT/bin/docker" <<'DOCKER'
#!/usr/bin/env bash
set -euo pipefail
[[ "$#" -eq 3 && "$1" == pull && "$2" == -- && "$3" == registry.example/test:1.0 ]] || exit 99
count="$(cat "$PULL_TEST_ROOT/count")"
count=$((count + 1))
echo "$count" > "$PULL_TEST_ROOT/count"
case "$(cat "$PULL_TEST_ROOT/scenario")" in
  success) exit 0 ;;
  rate-then-success)
    if [[ "$count" -lt 3 ]]; then echo 'toomanyrequests: retry-after: 240.981µs' >&2; exit 7; fi ;;
  rate-always) echo 'toomanyrequests' >&2; exit 7 ;;
  network-then-success)
    if [[ "$count" -eq 1 ]]; then echo 'connection reset by peer' >&2; exit 7; fi ;;
  missing) echo 'manifest unknown' >&2; exit 8 ;;
  denied) echo 'denied: requested access' >&2; exit 9 ;;
  other) echo 'failed to register layer: no space left on device' >&2; exit 10 ;;
esac
DOCKER
cat > "$PULL_TEST_ROOT/bin/sleep" <<'SLEEP'
#!/usr/bin/env bash
echo "$1" >> "$PULL_TEST_ROOT/delays"
SLEEP
chmod +x "$PULL_TEST_ROOT/bin/docker" "$PULL_TEST_ROOT/bin/sleep"
export PATH="$PULL_TEST_ROOT/bin:$PATH"

check_case() {
  local scenario="$1" expected_status="$2" expected_count="$3" expected_delays="$4"
  echo "$scenario" > "$PULL_TEST_ROOT/scenario"
  echo 0 > "$PULL_TEST_ROOT/count"
  : > "$PULL_TEST_ROOT/delays"
  local status=0
  bash "$script_dir/pull-image.sh" registry.example/test:1.0 > "$PULL_TEST_ROOT/output" 2>&1 || status=$?
  [[ "$status" -eq "$expected_status" ]]
  [[ "$(cat "$PULL_TEST_ROOT/count")" -eq "$expected_count" ]]
  [[ "$(paste -sd, "$PULL_TEST_ROOT/delays")" == "$expected_delays" ]]
  echo "Passed: $scenario"
}

check_case success 0 1 ''
check_case rate-then-success 0 3 '15,30'
check_case rate-always 7 4 '15,30,60'
check_case network-then-success 0 2 '15'
check_case missing 8 1 ''
check_case denied 9 1 ''
check_case other 10 1 ''

status=0
echo 0 > "$PULL_TEST_ROOT/count"
bash "$script_dir/pull-image.sh" > "$PULL_TEST_ROOT/output" 2>&1 || status=$?
[[ "$status" -eq 2 && "$(cat "$PULL_TEST_ROOT/count")" -eq 0 ]]
echo 'Passed: missing argument makes no Docker request'
