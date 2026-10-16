#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
mode=${1:---write}
format_root=$repo_root
verify_arguments=()
temporary_snapshot=''

# Check the index/commit itself so unstaged edits cannot hide formatting faults.
case "$mode" in
    --write) ;;
    --check) verify_arguments=(--verify-no-changes) ;;
    --check-index)
        temporary_snapshot=$(mktemp -d -t k-lani-bot-format.XXXXXXXX)
        git -C "$repo_root" checkout-index --all --prefix="$temporary_snapshot/"
        format_root=$temporary_snapshot
        verify_arguments=(--verify-no-changes)
        ;;
    --check-ref)
        [[ "${2:-}" =~ ^[0-9a-f]{40,64}$ ]] || { printf 'An exact commit hash is required.\n' >&2; exit 2; }
        temporary_snapshot=$(mktemp -d -t k-lani-bot-format.XXXXXXXX)
        git -C "$repo_root" archive "$2" | tar -xf - -C "$temporary_snapshot"
        format_root=$temporary_snapshot
        verify_arguments=(--verify-no-changes)
        ;;
    *) printf 'Usage: scripts/format.sh [--write|--check|--check-index|--check-ref COMMIT]\n' >&2; exit 2 ;;
esac
if [[ -n "$temporary_snapshot" ]]; then
    trap 'find "$temporary_snapshot" -depth -delete' EXIT
fi
cd "$format_root"
mapfile -t source_files < <(find src tests tools -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | sort)
if ! dotnet format whitespace "$format_root" --folder --include "${source_files[@]}" \
    "${verify_arguments[@]}" --verbosity minimal; then
    printf '\nFormatting failed. Run scripts/format.sh, inspect the diff, and stage the corrected files.\n' >&2
    exit 1
fi

# The whitespace formatter cannot shorten a line, so the line-length limit is checked on the same tree.
if [[ ${#verify_arguments[@]} -gt 0 ]]; then
    if ! dotnet run --project "$repo_root/tools/RepositoryGuard/RepositoryGuard.csproj" --verbosity quiet -- \
        --repo "$format_root" --mode line-length > /dev/null; then
        printf 'Lines over 140 characters found. Break them into named steps; see the file:line list above.\n' >&2
        exit 1
    fi
fi

# Folder formatting has no WINDOWS preprocessor symbol. Validate that branch
# explicitly so Windows-only code cannot evade the repository style check.
if ! dotnet format whitespace scripts/windows-format.csproj "${verify_arguments[@]}" --verbosity quiet; then
    printf 'Windows formatting failed. Run scripts/format.sh and stage the corrected files.\n' >&2
    exit 1
fi
