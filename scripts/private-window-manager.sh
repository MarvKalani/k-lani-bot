#!/usr/bin/env bash
# Supplies EWMH window discovery for transport tests inside an already isolated GUI session.
set -euo pipefail
umask 077

if [[ $# -eq 0 || "${K_LANI_BOT_PRIVATE_GUI_TEST:-}" != 1 \
    || -z "${K_LANI_BOT_PRIVATE_DISPLAY:-}" || "${DISPLAY:-}" != "$K_LANI_BOT_PRIVATE_DISPLAY" \
    || -z "${K_LANI_BOT_PRIVATE_RUNTIME:-}" || "${XDG_RUNTIME_DIR:-}" != "$K_LANI_BOT_PRIVATE_RUNTIME" ]]; then
    printf 'Run this command through scripts/private-gui-session.sh.\n' >&2
    exit 2
fi

# The regression itself reports missing wmctrl as a skip. If wmctrl is installed, its real Info call must run.
if ! command -v wmctrl >/dev/null; then
    exec "$@"
fi
if ! command -v openbox >/dev/null; then
    printf 'Transport GUI checks with wmctrl also require openbox in the private test environment.\n' >&2
    exit 2
fi

configuration="$XDG_RUNTIME_DIR/openbox.xml"
printf '<openbox_config xmlns="http://openbox.org/3.4/rc"/>\n' > "$configuration"
openbox --sm-disable --config-file "$configuration" > "$XDG_RUNTIME_DIR/openbox.log" 2>&1 &
window_manager=$!
trap 'kill "$window_manager" 2>/dev/null || true; wait "$window_manager" 2>/dev/null || true' EXIT

for attempt in {1..50}; do
    if wmctrl -m >/dev/null 2>&1; then
        "$@"
        exit 0
    fi
    sleep 0.1
done
printf 'Private window manager did not become ready.\n' >&2
exit 1
