#!/usr/bin/env bash
set -euo pipefail
umask 077
[ "$(uname -s)" = Darwin ] || { printf 'Diese Cartridge benötigt macOS 27.\n' >&2; exit 1; }
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source_bundle=${1:?Pfad zum geprüften k-lani-bot-node.app angeben}
shift
source_bundle=$(cd -- "$source_bundle" && pwd)
node="$source_bundle/Contents/MacOS/k-lani-bot-node"
manifest="$repo_root/cartridges/apple/macos-27-de/cartridge.json"
plan_dir=$(mktemp -d -t k-lani-bot-install-plan)
plan_file="$plan_dir/plan.json"
trap 'rm -f "$plan_file"; rmdir "$plan_dir"' EXIT
"$node" --cartridge "$manifest" --cartridge-action plan --source-bundle "$source_bundle" "$@" > "$plan_file"
source_pin=$(/usr/bin/plutil -extract source_sha256 raw -o - "$plan_file")
"$node" --cartridge "$manifest" --cartridge-action apply --source-bundle "$source_bundle" --source-sha256 "$source_pin" "$@"
