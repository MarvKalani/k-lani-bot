#!/bin/sh
set -eu
DBUS_SESSION_BUS_ADDRESS=$(cat /tmp/performance-bus.address)
export DBUS_SESSION_BUS_ADDRESS
exec "$@"
