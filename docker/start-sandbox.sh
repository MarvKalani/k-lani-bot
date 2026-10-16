#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
: "${K_LANI_BOT_BINARY:?Set the absolute path to the published k-lani-bot executable}"
: "${K_LANI_BOT_WORKSPACE:?Set a fresh isolated workspace}"
: "${K_LANI_BOT_STATE:?Set a fresh private state directory outside the workspace}"
"$K_LANI_BOT_BINARY" --init --workspace "$K_LANI_BOT_WORKSPACE" --state "$K_LANI_BOT_STATE" --node sandbox --port "${K_LANI_BOT_PORT:-5443}" --scopes "${K_LANI_BOT_SCOPES:-observe,ui,files.read,files.write,clipboard}" --sandbox-image "${K_LANI_BOT_IMAGE:-k-lani-bot-actuator:local}" --backup-requirement "${K_LANI_BOT_BACKUP_REQUIREMENT:-required}"
exec "$K_LANI_BOT_BINARY" --serve --config "$K_LANI_BOT_STATE/node.json"
