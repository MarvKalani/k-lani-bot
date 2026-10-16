#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
build_root=$repo_root
mode=${1:---check}
temporary_snapshot=''
case "$mode" in
    --check) ;;
    --check-index)
        temporary_snapshot=$(mktemp -d -t k-lani-bot-build.XXXXXXXX)
        git -C "$repo_root" checkout-index --all --prefix="$temporary_snapshot/"
        build_root=$temporary_snapshot
        ;;
    --check-ref)
        [[ "${2:-}" =~ ^[0-9a-f]{40,64}$ ]] || { printf 'An exact commit hash is required.\n' >&2; exit 2; }
        temporary_snapshot=$(mktemp -d -t k-lani-bot-build.XXXXXXXX)
        git -C "$repo_root" archive "$2" | tar -xf - -C "$temporary_snapshot"
        build_root=$temporary_snapshot
        ;;
    *) printf 'Usage: scripts/build.sh [--check|--check-index|--check-ref COMMIT]\n' >&2; exit 2 ;;
esac
if [[ -n "$temporary_snapshot" ]]; then
    trap 'find "$temporary_snapshot" -depth -delete' EXIT
fi
cd "$build_root"
source scripts/check-clock.sh
core_project=src/k-lani-bot-node/k-lani-bot-node.csproj
artifacts_root="$build_root/.artifacts/check-build"
mkdir -p "$artifacts_root/logs"
printf 'step\telapsed_ms\texit_code\n' > "$artifacts_root/timings.tsv"

build_project() {
    local label=$1
    local platform=$2
    local project=$3
    shift 3
    local started finished exit_code=0
    started=$(check_now_ms)
    dotnet build "$project" --artifacts-path "$artifacts_root/$platform" \
        -p:ArtifactsPivots=debug -warnaserror --nologo --verbosity quiet "$@" \
        > "$artifacts_root/logs/$label.log" 2>&1 || exit_code=$?
    finished=$(check_now_ms)
    printf '%s\t%s\t%s\n' "$label" "$((finished - started))" "$exit_code" >> "$artifacts_root/timings.tsv"
    if [[ "$exit_code" -ne 0 ]]; then
        cat "$artifacts_root/logs/$label.log" >&2
        return "$exit_code"
    fi
}

# Parallel first runs of the .NET CLI race while creating its shared first-use state on a fresh machine;
# this read-only command completes that setup once before the builds start.
dotnet nuget locals all --list > "$artifacts_root/logs/dotnet-first-use.log" 2>&1

# Each OS has separate assets/obj/bin. Parallel cross-builds cannot overwrite
# Linux outputs that the regression executables and their project references use.
# The Windows dialog regression only runs on Windows; it is built here so that it always compiles against the
# current backend.
{
    build_project core-windows windows "$core_project" -f net10.0-windows -p:TargetFrameworks=net10.0-windows -r win-x64 \
        && build_project windows-dialog-regression windows tests/WindowsDialogRegression/WindowsDialogRegression.csproj -r win-x64
} &
windows_build=$!
build_project core-macos macos "$core_project" -f net10.0 -p:TargetFrameworks=net10.0 -r osx-arm64 &
macos_build=$!
exit_code=0
build_project core-linux linux "$core_project" -f net10.0 -p:TargetFrameworks=net10.0 -r linux-x64 || exit_code=$?
if [[ "$exit_code" -eq 0 ]]; then
    build_project linux-projects linux scripts/build-projects.targets -r linux-x64 || exit_code=$?
fi
# Always join both cross-builds, including after a Linux failure.
wait "$windows_build" || exit_code=$?
wait "$macos_build" || exit_code=$?
if [[ "$exit_code" -ne 0 ]]; then
    exit "$exit_code"
fi
printf 'All projects built with warnings as errors. Timings: %s/timings.tsv\n' "$artifacts_root"
