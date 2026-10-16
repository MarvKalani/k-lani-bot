#!/usr/bin/env bash
# Runs inside scripts/private-gui-session.sh. Fails when the private session bus can start a host service other than
# the accessibility bus: such a service has no display there, aborts, and leaves a core dump for every request.
set -euo pipefail

allowed_services=(org.freedesktop.DBus org.a11y.Bus)

activatable_names=$(dbus-send --session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus \
    org.freedesktop.DBus.ListActivatableNames | sed -n 's/.*string "\(.*\)"/\1/p')

unexpected_names=""
while IFS= read -r activatable_name; do
    if [[ -n "$activatable_name" && ! " ${allowed_services[*]} " =~ " $activatable_name " ]]; then
        unexpected_names+="$activatable_name"$'\n'
    fi
done <<< "$activatable_names"

if [[ -n "$unexpected_names" ]]; then
    printf 'The private session bus can start host services:\n%s' "$unexpected_names" >&2
    exit 1
fi
printf 'Private session bus activates only: %s\n' "${allowed_services[*]}"
