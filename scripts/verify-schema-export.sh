#!/usr/bin/env bash
# mcp/schemas is generated from contracts/tools.json for clients that read schema files directly.
# A stale export silently advertises arguments the node no longer accepts (or hides new tools),
# so the committed files must equal a fresh export.
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
node_binary=${1:?Usage: scripts/verify-schema-export.sh <k-lani-bot-node.dll|executable>}
export_directory=$(mktemp -d -t k-lani-bot-schemas.XXXXXXXX)
trap 'rm -rf "$export_directory"' EXIT
if [[ "$node_binary" == *.dll ]]; then
    dotnet "$node_binary" --export-schemas "$export_directory"
else
    "$node_binary" --export-schemas "$export_directory"
fi
if ! diff -r "$repo_root/mcp/schemas" "$export_directory"; then
    printf '\nmcp/schemas is out of date. Regenerate it with:\n  %s --export-schemas mcp/schemas\n' "$node_binary" >&2
    exit 1
fi
printf 'mcp/schemas matches contracts/tools.json.\n'
