#!/usr/bin/env bash
set -euo pipefail
[ "$(uname -s)" = Darwin ] || { printf 'Bundle signing requires macOS.\n' >&2; exit 1; }
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source_binary=${1:?Supply the published osx-arm64 k-lani-bot-node binary}
output_dir=${2:?Supply a fresh output directory}
bundle="$output_dir/k-lani-bot-node.app"
[ ! -e "$bundle" ] || { printf 'Existing bundle preserved; use a fresh output directory.\n' >&2; exit 1; }
stage="$output_dir/.k-lani-bot-node.app.staging"
[ ! -e "$stage" ] || { printf 'Existing staging directory preserved.\n' >&2; exit 1; }
mkdir -p "$stage/Contents/MacOS"
cp "$source_binary" "$stage/Contents/MacOS/k-lani-bot-node"
chmod 755 "$stage/Contents/MacOS/k-lani-bot-node"
cp "$repo_root/tests/Apple/Bundle/Info.plist" "$stage/Contents/Info.plist"
# The iPhone Mirroring helper captures one window and recognizes its text with Apple Vision; nested code is signed first.
xcrun swiftc -O -o "$stage/Contents/MacOS/k-lani-bot-mirror" "$repo_root/src/k-lani-bot-mirror/main.swift"
codesign --force --sign "${K_LANI_BOT_MAC_SIGN_IDENTITY:--}" --identifier com.k-lani.bot.mirror "$stage/Contents/MacOS/k-lani-bot-mirror"
codesign --force --sign "${K_LANI_BOT_MAC_SIGN_IDENTITY:--}" --identifier com.k-lani.bot.node "$stage"
codesign --verify --strict "$stage"
mv "$stage" "$bundle"
printf '%s\n' "$bundle"
