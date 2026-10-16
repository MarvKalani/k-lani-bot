#!/usr/bin/env bash
set -euo pipefail
umask 077
ulimit -c 0
test "${K_LANI_BOT_PRIVATE_GUI_TEST:-}" = 1 || {
    printf 'Use scripts/private-gui-session.sh for the credential probe.\n' >&2
    exit 1
}
command -v gnome-keyring-daemon >/dev/null
command -v gdbus >/dev/null
probe_root=$(mktemp -d -t k-lani-secret-probe.XXXXXXXX)
keyring_process=''
cleanup() {
    if [[ -n "$keyring_process" ]]; then
        kill "$keyring_process" 2>/dev/null || true
        wait "$keyring_process" 2>/dev/null || true
    fi
    rm -rf -- "$probe_root"
}
trap cleanup EXIT
export XDG_DATA_HOME="$probe_root/data"
mkdir -p "$XDG_DATA_HOME/keyrings" "$probe_root/control"
printf '%s' 'synthetic-private-fixture-password' | gnome-keyring-daemon --foreground --unlock \
    --components=secrets --control-directory "$probe_root/control" > "$probe_root/keyring.log" 2>&1 &
keyring_process=$!
ready=false
for attempt in {1..40}; do
    if gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus \
        --method org.freedesktop.DBus.NameHasOwner org.freedesktop.secrets 2>/dev/null | grep -q true; then
        ready=true
        break
    fi
    sleep 0.1
done
test "$ready" = true || { printf 'Private keyring did not become ready.\n' >&2; exit 1; }
K_LANI_SECRET_PROBE=isolated dotnet "$1" --credential-provider-probe
