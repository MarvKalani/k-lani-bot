#!/usr/bin/env bash
# These Linux development checks use uptime so clock synchronization cannot
# turn a short test duration negative.
check_now_ms() {
    local uptime ignored seconds fraction
    read -r uptime ignored < /proc/uptime
    seconds=${uptime%.*}
    fraction=${uptime#*.}
    printf '%s\n' "$((seconds * 1000 + 10#$fraction * 10))"
}
