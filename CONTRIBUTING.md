# Contributing to k-lani-bot

Read [AGENTS.md](AGENTS.md) for the code standard and the [authoring guide](docs/AGENT-AUTHORING.md) for the product's purpose.
Contributions should make application actions reusable through MCP and keep deterministic work outside model context.
Use the repository's [Business Source License 1.1](LICENSE.md); this is source-available software, not an OSI open-source release.
Non-production development and testing remain permitted for business contributors. The private-production grant and
separate license requirement for business production are explained in [the license model](docs/legal/LICENSING.md).

## Report or propose a change

For a bug, include the source commit, operating system, actual application/build, MCP route, reproducible synthetic inputs,
expected result and observed result. State whether any application change completed before failure. Remove private values
from screenshots and logs. Use [private reporting](SECURITY.md) for security issues.

If a source, cartridge or deployment review reveals a concrete issue, please send an actionable report to
**support@kalanis.de**. Include the reviewed version/commit or digest, the relevant code or operation, expected versus
observed behavior, and a minimal reproduction where available. Label an AI-generated suspicion as unverified unless you
have checked it. Share the relevant sanitized findings rather than an unfiltered review transcript. A reviewer is not
expected to prove the absence of all bugs or to provide a fix before reporting an issue.

For a feature, describe the user operation, application capability and compact result contract. Distinguish a proposed API
from an existing tool. Broad new integrations benefit from an issue describing scope before implementation.

## Develop and verify

Use the SDK pinned in [global.json](global.json), Node.js 22+, Python 3 and Docker for development checks. Linux native tests
need the [desktop helpers](docs/GETTING-STARTED.md#build), Xvfb, Openbox and the private-session wrapper. Transport checks
start Openbox inside that private session so `wmctrl` can read the window-manager properties. Native credential tests
also need `gnome-keyring-daemon` and `gdbus`; absent prerequisites are reported as skipped, not passed.

After cloning, enable the checked-in commit and push guards:

```bash
scripts/install-git-guards.sh
```

The installer fetches the pinned scanner and configures `.githooks`. Check Git status and establish a verified backup of
the data/configuration affected by your change. Preserve existing private access; do not initialize an already registered node.
Local code work does not require a model login, a production desktop or a model-token measurement.

```bash
scripts/format.sh
scripts/format.sh --check
scripts/check.sh
```

`check.sh` builds every project with warnings as errors, runs deterministic checks and isolates GUI tests. Never run GUI
regressions on the operator's desktop. Keep a missing native platform check distinct from a successful cross-build.
For a focused standalone regression use `ulimit -c 0` before its command. Do not bypass hooks or suppress checks to commit.

## Submit

Describe the concrete before/after behavior, relevant tests and remaining limits. Keep code small and readable; follow
the analyzers rather than adding blanket suppressions. Changes to a gateway, setup or measurement route must update its
guide and runbook in the same change. A cartridge needs observed application/build evidence and fresh-input reuse checks.
Do not claim token savings from byte counts or reduced call counts; model measurements are separate, explicitly bounded work.

Commit only reviewed source, public documentation and sanitized fixtures. `.internal`, `.artifacts`, pairing state,
credentials and raw traces stay private. Maintainer task files mentioned in `AGENTS.md` are optional local records and
are intentionally absent from public clones. Public issues and pull requests do not require access to them.

Keep example configurations synthetic: use addresses under `example.com`, `example.org`, `example.net`, `.example`,
`.invalid` or `.test`. Copy a template to an ignored `*.local.json` file before adding real accounts or preferences.
Raw mail, database files, captures and exports belong in ignored private directories, never in the example configuration.
Ignore rules prevent future tracking; they do not remove data from existing commits, releases or previously shared copies.
