#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
image=${1:?Supply the built optional image to assess}
image_id=$(docker image inspect "$image" --format '{{.Id}}')
[[ "$image_id" =~ ^sha256:[0-9a-f]{64}$ ]] || exit 2
"$repo_root/scripts/supply-chain-check.sh" "$image_id" "$repo_root/.artifacts/laya-release-security"
printf 'Supply-chain gate passed for the exact image. Model calibration and independent security review remain separate requirements.\n'
