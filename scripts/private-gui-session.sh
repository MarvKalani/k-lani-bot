#!/usr/bin/env bash
# Runs one command inside a private GUI session: its own X server, its own D-Bus and its own
# XDG runtime directory. GUI regressions create real windows, so they must never be able to
# reach the operator's compositor or X server, whatever environment the caller brings.
#
# Why GDK_BACKEND and XDG_RUNTIME_DIR matter and not only DISPLAY: on a Wayland desktop GTK
# keeps using the Wayland backend when GDK_BACKEND=wayland is inherited. libwayland then falls
# back to the "wayland-0" socket inside XDG_RUNTIME_DIR even though WAYLAND_DISPLAY was unset,
# which is the operator's real session. A private, empty runtime directory removes that socket.
set -euo pipefail

if [[ $# -eq 0 ]]; then
    printf 'Usage: scripts/private-gui-session.sh <command> [arguments...]\n' >&2
    exit 2
fi
for required_tool in dbus-run-session xvfb-run bash; do
    if ! command -v "$required_tool" > /dev/null; then
        printf '%s is required for a private GUI session.\n' "$required_tool" >&2
        exit 2
    fi
done

runtime_directory=$(mktemp -d -t k-lani-bot-gui-runtime.XXXXXXXX)
chmod 700 "$runtime_directory"

# D-Bus activation inside the session may start gvfsd-fuse, which mounts <runtime>/gvfs and only
# unmounts it a moment after the bus is gone. Removing the directory too early fails with a
# misleading "Is a directory", and a failing trap would turn a passing test into exit code 1.
remove_runtime_directory() {
    local attempt
    for attempt in 1 2 3 4 5; do
        if mountpoint -q "$runtime_directory/gvfs" 2> /dev/null; then
            fusermount3 -u -z "$runtime_directory/gvfs" 2> /dev/null || true
        fi
        if rm -rf "$runtime_directory" 2> /dev/null; then
            return 0
        fi
        sleep 0.2
    done
    printf 'Warning: could not remove %s\n' "$runtime_directory" >&2
    return 0
}
trap remove_runtime_directory EXIT

# The private bus may activate only the services the tests use. With the host's full service list it also starts
# the operator's desktop services (KDE portal, wallet, secret service). They inherit XDG_CURRENT_DESKTOP but have
# no display, so Qt aborts and every request leaves a core dump behind.
readonly activatable_bus_services=(org.a11y.Bus)

find_bus_service_file() {
    local service_name=$1
    local data_directories
    local data_directory
    IFS=: read -r -a data_directories <<< "${XDG_DATA_DIRS:-/usr/local/share:/usr/share}:/usr/share"
    for data_directory in "${data_directories[@]}"; do
        if [[ -f "$data_directory/dbus-1/services/$service_name.service" ]]; then
            printf '%s\n' "$data_directory/dbus-1/services/$service_name.service"
            return 0
        fi
    done
    return 1
}

# Writes the bus configuration to $1; its only service directory holds copies of the allowed service files.
write_private_bus_configuration() {
    local configuration_file=$1
    local service_directory="$runtime_directory/bus-services"
    local service_name
    local service_file
    mkdir -p "$service_directory"
    for service_name in "${activatable_bus_services[@]}"; do
        if ! service_file=$(find_bus_service_file "$service_name"); then
            printf 'D-Bus service file for %s was not found; GUI tests cannot reach the accessibility bus.\n' \
                "$service_name" >&2
            exit 2
        fi
        cp "$service_file" "$service_directory/"
    done
    cat > "$configuration_file" << EOF
<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
<busconfig>
  <type>session</type>
  <keep_umask/>
  <listen>unix:tmpdir=/tmp</listen>
  <auth>EXTERNAL</auth>
  <servicedir>$service_directory</servicedir>
  <policy context="default">
    <allow send_destination="*" eavesdrop="true"/>
    <allow eavesdrop="true"/>
    <allow own="*"/>
  </policy>
  <limit name="max_incoming_bytes">1000000000</limit>
  <limit name="max_outgoing_bytes">1000000000</limit>
  <limit name="max_message_size">1000000000</limit>
  <limit name="service_start_timeout">120000</limit>
  <limit name="auth_timeout">240000</limit>
</busconfig>
EOF
}

private_bus_configuration="$runtime_directory/session-bus.conf"
write_private_bus_configuration "$private_bus_configuration"

# The tests read these two variables back and refuse to open a window unless the display and
# runtime directory they see are exactly the ones created here (see tests/PrivateGuiSession.cs).
# DISPLAY is only known once xvfb-run has chosen a free display, hence the inner shell.
record_private_display='export K_LANI_BOT_PRIVATE_DISPLAY="$DISPLAY"; exec "$@"'

exit_code=0
env \
    -u DISPLAY \
    -u WAYLAND_DISPLAY \
    -u XAUTHORITY \
    -u DBUS_SESSION_BUS_ADDRESS \
    -u SESSION_MANAGER \
    -u QT_QPA_PLATFORM \
    -u XDG_SESSION_TYPE \
    XDG_RUNTIME_DIR="$runtime_directory" \
    GDK_BACKEND=x11 \
    GIO_USE_VFS=local \
    GSETTINGS_BACKEND=memory \
    K_LANI_BOT_PRIVATE_GUI_TEST=1 \
    K_LANI_BOT_PRIVATE_RUNTIME="$runtime_directory" \
    dbus-run-session --config-file "$private_bus_configuration" -- \
    xvfb-run -a -s '-screen 0 1280x720x24' \
    bash -c "$record_private_display" private-gui-session "$@" || exit_code=$?
exit "$exit_code"
