#!/bin/sh
set -eu
if [ ! -s /run/secrets/laya-api-key ]; then
    echo 'Laya requires a private API credential file.' >&2
    exit 1
fi
LAYA_API_KEY=$(cat /run/secrets/laya-api-key)
export LAYA_API_KEY
exec python -m laya.serve
