# Security reporting

Report a suspected vulnerability privately to **support@kalanis.de**, with the subject `k-lani-bot security report`,
or through GitHub's private **Report a vulnerability** form in this repository's Security tab.
Do not post credentials, node registries, personal screenshots, mail contents or exploit details in public issues.
For sensitive evidence, first send a short description and arrange a suitable transfer with the maintainer.

Include the source commit or package digest, operating system, application/build, affected access route, prerequisites,
expected boundary and observed behavior. Prefer a minimal reproduction using synthetic data. Keep raw logs private;
redact secrets even in a private initial report. Do not test against other people's systems.

Reviewers, including those using AI assistance, are asked to report concrete security findings through this private
channel. State what was actually checked and whether the finding is reproduced, supported by code evidence or still an
unverified suspicion. Do not publish sensitive exploit details or forward an unfiltered transcript containing private data.

The planned Git publication is a developer source alpha. Existing exact-binary maintainer evidence covers a private
[Linux/X11 candidate](.release/public-security-profile.json); that candidate is not selected as a public download.
Its assessment does not qualify every source platform and is not an independent security audit or certification.
There is no long-term supported release or promised response/fix deadline. Older alpha snapshots and other platforms
have no implied security-maintenance guarantee. Fixes and any affected-release guidance will be documented when assessed.
This description does not override expressly agreed services or mandatory legal obligations. Source access and third-party
or AI-assisted reviews help evaluate a deployment; they do not constitute a blanket safety certification.

The node enforces its configured access policy and operating-system permissions. The desktop profile can operate broadly
within those rights. Keep private state outside the operated workspace and qualify new actions with disposable data.
The closed-file backup and credential components do not yet provide a mandatory backup gate or complete administrative
credential workflow. See [current boundaries](docs/ADMINISTRATION-FOUNDATION.md) and [operator guidance](docs/legal/OPERATOR-SAFETY.md).

Community-cartridge automatic activation is outside the release profile. A signature or publisher badge is not local
acceptance of its code and dependencies. Do not activate an unfamiliar downloaded cartridge in a production session.

Maintainers run both the repository guard and the pinned secret scanner before publication. Full Git history, commit
messages and attachments matter, not only the current working tree. A clean scan is one check, not proof of absence of vulnerabilities.
