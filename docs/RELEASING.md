# Preparing a developer alpha

The planned Git publication contains the **source tree and build instructions for k-lani-bot and its node component**.
It retains both MCP/controller and node implementations for Windows, macOS and Linux, the Android host bridge and the
experimental iOS Simulator bridge. Controller platform support is not limited to the Linux candidate used in testing.
No prebuilt Linux archive or Docker image is selected as a public release asset. Those remain private test/candidate
artifacts. Other platform binaries are not selected by this source-publication decision either.

Publishing source or a separately selected binary is an explicit maintainer action; none of the commands below pushes
source, creates a GitHub release or changes repository visibility. Existing binary evidence uses the
[core-linux-x11](../.release/public-security-profile.json) profile. That profile describes its tested candidate, not the
whole product, a public download selection, or acceptance of other platforms. No independent certification is claimed.

## Source publication

Keep a verified private backup of the repository and required ignored work before changing publication state.
Review the complete tracked file list, license, public contact information, application limits and benchmark scope.
Private tasks, credentials, raw traces and release-review packets must remain excluded.
Keep the Linux implementation and test tooling in the source tree. Do not attach the historical Linux candidate,
Docker image, private distribution directory or assessment packet to the Git release. A Linux build can later be
selected as a platform package after explicit scope selection and acceptance; it is not an extra service required by MCP.

Check that the selected commit, archive, license grant and business-facing documents describe the same version and rights.
A change to the Additional Use Grant requires a new reviewed source/package candidate; never replace LICENSE.md inside an
old reviewed archive. The old version's terms and its evidence remain unchanged. Unapproved commercial contract drafts
remain private and are not part of the source delivery.

Install [the guards](../scripts/install-git-guards.sh), then run:

```bash
scripts/format.sh
scripts/format.sh --check
scripts/check.sh
scripts/repository-guard.sh --mode history
scripts/gitleaks.sh history
```

The checks must also cover the final commit. Commit hooks validate the staged snapshot; push hooks validate outgoing
commits. [CI](../.github/workflows/release-checks.yml) repeats source guards and regressions. A scan only after publishing
would be too late. Configure repository protections when creating the remote; local hooks alone cannot enforce them.

The guard also stops a file above 1 MiB and a published history above 10 MiB, counting each file version once,
zlib-compressed (limits in [`.release/repository-size.json`](../.release/repository-size.json)). A sudden jump usually
means a build output, log or dump was committed; find it first, then raise the limit by a reviewed commit if it belongs.

The Linux regression host needs Pillow for synthetic image fixtures.
CI installs Ubuntu's `python3-pil` and uses `/usr/bin/python3` through `K_LANI_BOT_TEST_PYTHON`; local checks default to `python3`.
These are test dependencies, not node runtime requirements. Earlier-version state fixtures are reviewed `.txt` templates;
tests materialize runtime filenames in temporary directories so ignored private policy files cannot mask a missing fixture.

A fresh public history does not erase private development evidence. Historical benchmark and application-acceptance
records can identify pre-publication commits retained in the maintainer's private archive. They describe their recorded
build and date, not an acceptance of a newer binary. Do not relabel old receipts with the new initial commit.

## Optional local Linux candidate acceptance

This section preserves the existing Linux verification route for development. It is not part of the selected public
source delivery. `scripts/release.sh` requires `--linux-candidate` explicitly; a bare invocation or the former
`--alpha` invocation refuses before packaging. This prevents the old Linux-only default from defining the product release.

Use a clean committed working tree. Provision the reviewed Syft and Grype executables and export their absolute paths as
`K_LANI_BOT_SYFT` and `K_LANI_BOT_GRYPE`; [pinned hashes](../.release/security-tools.json) are checked before use.
Docker, the Linux desktop test dependencies and an up-to-date vulnerability database are required. No model run is involved.
Transport checks use `private-window-manager.sh` inside the private GUI session. With `wmctrl` installed, Openbox is also
required; a bare Xvfb display has no EWMH window list. The package's exact-binary transport check runs inside the qualified
Docker desktop, with a self-contained test driver and read-only mounts for the candidate, contracts and session scripts.
It therefore exercises native compact Info even when the ordinary host regression reports missing `wmctrl` as a skip.

```bash
scripts/prepare-public-security-review.sh
scripts/release.sh --linux-candidate --alpha
```

Preparation runs format/build/regression and complete source-history checks, publishes the self-contained executable,
adds legal notices, tests the exact executable in a private GUI session and Docker worker, and assesses the binary and
worker dependencies. Missing, stale or blocking scan evidence fails the gate. Do not suppress findings to obtain a package.

The immutable packet is `.artifacts/public-security-review/<source-commit>/core-linux-x11/`, containing `candidate/` and
private `review/` evidence. The release command validates its commit, file hashes and assessment, then copies those exact
bytes to `dist-alpha-linux/`. It refuses to overwrite an existing destination. Keep earlier candidates under distinct
private names; do not rebuild a reviewed candidate or copy an assessment from a different commit.

The `--linux-candidate --signed` mode additionally requires an existing external signing key and configured reviewer. An unsigned alpha
does not claim an authenticated publisher. Generate transport/archive checksums only after final package assembly;
checksums establish byte identity, not publisher authenticity.

Before upload, verify the archive by extraction and hash comparison, and confirm a fresh source clone has the expected
root/history, public files and no private state. Upload only intended source/package artifacts, never the private review
directory. A completed local assessment is not itself a publication.
