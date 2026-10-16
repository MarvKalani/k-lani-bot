#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
old_path=$(git config --get core.hooksPath || true)
if [ -n "$old_path" ] && [ "$old_path" != .githooks ]; then
    printf 'Existing hooksPath preserved; explicitly integrate .githooks before installing.\n' >&2
    exit 2
fi
command -v dotnet >/dev/null
command -v docker >/dev/null
dotnet build tools/RepositoryGuard/RepositoryGuard.csproj --verbosity quiet --nologo
# Provision deliberately during setup; hook execution never downloads a scanner.
docker pull ghcr.io/gitleaks/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f
git config core.hooksPath .githooks
printf 'Index, commit-message and outgoing-history checks enabled. Local hooks remain bypassable; configure server protections too.\n'
