#!/usr/bin/env bash
# Lists, and on request removes, old entries in .artifacts/. Builds, publishes and checks write there and nothing
# ever cleaned it up, so it grew to several gigabytes of earlier stages.
#
# The default is a dry run. What may go is decided by the operator, not by this script: build intermediates are
# removed with --delete, release packages only with --include-packages, and evidence (private packages, review and
# security material, screenshots, commit and gitleaks logs) only with --include-evidence. Entries the script cannot
# classify are listed and never removed.
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
artifacts="$repo_root/.artifacts"
older_than_days=2
delete=false
include_packages=false
include_evidence=false

usage() {
    printf 'Usage: scripts/clean-artifacts.sh [--older-than DAYS] [--delete] [--include-packages] [--include-evidence]\n'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --older-than)
            older_than_days=${2:?--older-than needs a number of days}
            [[ "$older_than_days" =~ ^[0-9]+$ ]] || { usage >&2; exit 2; }
            shift 2
            ;;
        --delete) delete=true; shift ;;
        --include-packages) include_packages=true; shift ;;
        --include-evidence) include_evidence=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done

[[ -d "$artifacts" ]] || { printf 'No .artifacts directory.\n'; exit 0; }

# Evidence is checked first: a private package is evidence even though it is also a package.
classify() {
    local name=$1
    case "$name" in
        *PRIVATE*|private-*|*assessment*|*profile*|audit-*|*security*|*gitleaks*|*-commit.log|*.png|*.ips)
            printf 'evidence' ;;
        *.zip|*.tar.gz|*-linux-x64|*-win-x64|*-windows-x64|*-osx-arm64|*-metadata.json)
            printf 'package' ;;
        check-build|check-performance|dev-*|*publish*|*-build|*-build.log|runtime|bin|obj)
            printf 'build' ;;
        *)
            printf 'unclassified' ;;
    esac
}

may_remove() {
    case "$1" in
        build) [[ "$delete" == true ]] ;;
        package) [[ "$delete" == true && "$include_packages" == true ]] ;;
        evidence) [[ "$delete" == true && "$include_evidence" == true ]] ;;
        *) return 1 ;;
    esac
}

now=$(date +%s)
total_kb=0
removed_kb=0
printf '%-12s %6s %9s  %s\n' category age_d size_kb entry
while IFS= read -r -d '' entry; do
    name=$(basename -- "$entry")
    modified=$(stat -c %Y -- "$entry")
    age_days=$(( (now - modified) / 86400 ))
    (( age_days >= older_than_days )) || continue
    category=$(classify "$name")
    size_kb=$(du -sk -- "$entry" | cut -f1)
    total_kb=$(( total_kb + size_kb ))
    action=""
    if may_remove "$category"; then
        rm -rf -- "$entry"
        removed_kb=$(( removed_kb + size_kb ))
        action="  (removed)"
    fi
    printf '%-12s %6s %9s  %s%s\n' "$category" "$age_days" "$size_kb" "$name" "$action"
done < <(find "$artifacts" -mindepth 1 -maxdepth 1 -print0 | sort -z)

printf '\nOlder than %s days: %s MB listed, %s MB removed.\n' "$older_than_days" $(( total_kb / 1024 )) $(( removed_kb / 1024 ))
if [[ "$delete" != true ]]; then
    printf 'Dry run. Add --delete to remove build intermediates; packages and evidence need their own option.\n'
fi
printf 'Note: src/k-lani-bot-node/obj may hold output named after earlier product names; a rebuild recreates obj/.\n'
