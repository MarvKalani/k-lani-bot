#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
: "${K_LANI_BOT_SYFT:?Supply the reviewed Syft binary outside the source repository}"
: "${K_LANI_BOT_GRYPE:?Supply the reviewed Grype binary outside the source repository}"
[[ "$#" == 2 ]] || { printf 'Usage: scripts/supply-chain-check.sh subject output-prefix\n' >&2; exit 2; }
dotnet run --project "$repo_root/tools/k-lani-bot-supplychain/k-lani-bot-supplychain.csproj" --verbosity quiet -- "$1" "$2" "$K_LANI_BOT_SYFT" "$K_LANI_BOT_GRYPE" "$repo_root/.release/security-tools.json"
