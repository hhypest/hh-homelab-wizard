#!/usr/bin/env bash
set -euo pipefail

# Run from the checkout root. Only the existing acquisition helper retries a registry request.
if [[ $# -eq 0 ]]; then
  echo "Usage: bash pull-profile-images.sh <service-id> [...]" >&2
  exit 2
fi

tool="tools/HhHomelab.Wizard.ProfileCheck"
profile="profiles/hh-homelab/1.3.0/research-profile.json"
dotnet restore "$tool" --locked-mode
dotnet build "$tool" --no-restore --configuration Release

# Command substitution propagates selection failure before any Docker call, including partial output.
images="$(dotnet run --project "$tool" --no-build --configuration Release -- --images "$profile" "$@")"
if [[ -z "$images" ]]; then
  echo "Runtime image selection returned no images" >&2
  exit 1
fi
while IFS= read -r image; do
  bash .github/scripts/pull-image.sh "$image"
done <<< "$images"
