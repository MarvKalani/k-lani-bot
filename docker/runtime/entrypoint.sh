#!/bin/sh
set -eu
case "${1:-}" in
    --*) set -- /app/k-lani-bot-node "$@" ;;
esac
mkdir -p /tmp/home /tmp/config /tmp/cache /tmp/data /tmp/dotnet /tmp/runtime /tmp/.X11-unix
chmod 700 /tmp/runtime /tmp/home
chmod 1777 /tmp/.X11-unix
# Activated accessibility services inherit the bus daemon's descriptors; stdout must stay a protocol stream.
dbus-daemon --session --fork --print-address=3 3>/tmp/performance-bus.address >&2
DBUS_SESSION_BUS_ADDRESS=$(cat /tmp/performance-bus.address)
export DBUS_SESSION_BUS_ADDRESS
Xvfb :99 -screen 0 1280x720x24 -nolisten tcp >&2 &
ready=0
for attempt in $(seq 1 50); do
    if xdotool getdisplaygeometry >/dev/null 2>&1; then ready=1; break; fi
    sleep 0.1
done
test "$ready" = 1 || exit 3
fluxbox >&2 &
gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress >&2
exec "$@"
