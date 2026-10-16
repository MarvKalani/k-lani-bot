#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
image=ghcr.io/gitleaks/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f
mode=${1:-history}
case "$mode" in
    index) scan_args=(git --pre-commit --staged) ;;
    history)
        if [ "$#" -gt 1 ]; then
            [[ "$2" =~ ^[0-9a-f]{40,64}$ ]] || { printf 'Explicit commit hash required.\n' >&2; exit 2; }
            scan_args=(git "--log-opts=$2")
        else scan_args=(git --log-opts=--all); fi ;;
    *) printf 'Unknown scanner mode.\n' >&2; exit 2 ;;
esac
command -v docker >/dev/null || { printf 'Release secret scan requires Docker with the pinned Gitleaks image; core k-lani-bot has no Gitleaks runtime dependency.\n' >&2; exit 2; }
docker run --pull never --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
    --user "$(id -u):$(id -g)" --tmpfs /tmp:rw,noexec,nosuid,size=32m \
    --mount "type=bind,src=$repo_root,dst=/repo,readonly" --workdir /repo "$image" \
    "${scan_args[@]}" --config /repo/.release/gitleaks.toml --gitleaks-ignore-path /dev/null \
    --ignore-gitleaks-allow --redact=100 --no-color --no-banner --timeout 120 /repo
