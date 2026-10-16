#!/bin/sh
set -eu
if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ]; then
    dbus-daemon --session --fork --print-address=3 3>/tmp/k-lani-bot-bus.address >&2
    DBUS_SESSION_BUS_ADDRESS=$(cat /tmp/k-lani-bot-bus.address)
    export DBUS_SESSION_BUS_ADDRESS
fi
mkdir -p /tmp/config /tmp/cache /tmp/data /tmp/dotnet /tmp/.X11-unix
chmod 1777 /tmp/.X11-unix
Xvfb :99 -screen 0 1920x1080x24 -nolisten tcp >&2 &
ready=0
for attempt in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    if xdotool getdisplaygeometry >/dev/null 2>&1; then ready=1; break; fi
    sleep 0.1
done
if [ "$ready" != 1 ]; then echo 'Virtual display failed readiness.' >&2; exit 1; fi
openbox >&2 &
# Activate the accessibility bus before the actuator probes capabilities.
gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress >/dev/null
exec /app/k-lani-bot-node "$@"
