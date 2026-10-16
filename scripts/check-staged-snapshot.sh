#!/usr/bin/env bash
# Runs a command (default: the full scripts/check.sh) on a clean copy of what is staged, in a throw-away
# repository. The result then does not depend on other edits in the working tree, for example a half
# finished change to the branding assets that the build embeds. It is what the commit hook builds and
# what CI will see.
#
# Usage: scripts/check-staged-snapshot.sh [command and arguments, relative to the repository root]
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
snapshot_dir=${K_LANI_BOT_SNAPSHOT_DIR:-${TMPDIR:-/tmp}/k-lani-bot-staged-snapshot}
rm -rf "$snapshot_dir"
mkdir -p "$snapshot_dir"
git -C "$repo_root" checkout-index -a --prefix="$snapshot_dir/"
cd "$snapshot_dir"
# Some tests list the files of the repository with git, so the copy has to be one.
git init -q
git add -A
git -c user.name=snapshot -c user.email=snapshot@example.invalid commit -q -m snapshot
scripts/format.sh --check
if [[ $# -eq 0 ]]; then
    scripts/check.sh
else
    "$@"
fi
