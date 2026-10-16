#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
# Source publication does not select a platform binary. Keep candidate packaging explicit.
if [[ "${1:-}" != --linux-candidate || $# -gt 2 ]]; then
    printf 'Usage: scripts/release.sh --linux-candidate [--alpha|--signed]\n' >&2
    printf 'The planned Git publication contains source; no Linux download is selected by default. See docs/RELEASING.md.\n' >&2
    exit 2
fi
mode=${2:---alpha}
[[ "$mode" == --alpha || "$mode" == --signed ]] || { printf 'Expected --alpha or --signed.\n' >&2; exit 2; }
source_commit=$(git rev-parse HEAD)
packet="$repo_root/.artifacts/public-security-review/$source_commit/core-linux-x11"
candidate="$packet/candidate"
review="$packet/review"
review_project=tools/k-lani-bot-release-review/k-lani-bot-release-review.csproj
git diff --quiet && git diff --cached --quiet || { printf 'Release requires clean reviewed source.\n' >&2; exit 2; }
scripts/repository-guard.sh --mode history --ref "$source_commit"
scripts/gitleaks.sh history "$source_commit"
dotnet run --project "$review_project" --verbosity quiet -- verify-candidate \
    "$candidate" .release/public-security-profile.json "$review/assessment.json" "$source_commit"

if [[ "$mode" == --signed ]]; then
    : "${K_LANI_BOT_RELEASE_SIGNING_KEY:?Set the release signing key outside the repository}"
    : "${K_LANI_BOT_RELEASE_REVIEWER:?Set the locally trusted reviewer JSON outside the candidate}"
    dotnet run --project "$review_project" --verbosity quiet -- verify \
        "$candidate" .release/public-security-profile.json "$review/assessment.json" "$review/approval.json" \
        "$K_LANI_BOT_RELEASE_REVIEWER" "$source_commit"
fi

# Rebuilding after the checks would create different, unreviewed bytes.
# Alpha labelling removes a certification claim, never the technical gates.
destination=dist-alpha-linux
[[ ! -e "$destination" ]] || { printf 'dist-alpha-linux already exists; choose a clean release destination.\n' >&2; exit 2; }
mkdir "$destination"
cp -a "$candidate/." "$destination/"
if [[ "$mode" == --signed ]]; then
    "./$destination/k-lani-bot-node" --inventory "$repo_root/$destination" --signing-key "$K_LANI_BOT_RELEASE_SIGNING_KEY" > .artifacts/release-alpha-linux.json
else
    "./$destination/k-lani-bot-node" --inventory "$repo_root/$destination" > .artifacts/release-alpha-linux.json
fi
printf 'Linux ALPHA candidate copied for local use. It is not selected for public distribution; no publication or certification claimed.\n'
