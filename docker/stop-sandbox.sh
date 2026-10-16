#!/usr/bin/env bash
set -euo pipefail
: "${K_LANI_BOT_CONTAINER:?Provide the exact managed container name}"
label=$(docker inspect --format '{{index .Config.Labels "k-lani-bot.managed"}}' "$K_LANI_BOT_CONTAINER")
if [ "$label" != true ]; then echo 'Refusing to remove an unmanaged container.' >&2; exit 1; fi
docker rm -f -- "$K_LANI_BOT_CONTAINER"
