# k-lani-bot: purpose and operating responsibility

k-lani-bot is an automation and administration tool. It operates other programs and operating-system functions within the
configured permissions. This expressly includes state-changing and irreversible operations: deleting or overwriting files,
installing or uninstalling software, changing configuration and permissions. An expressly ordered deletion or uninstallation
is an intended function.

An AI can misunderstand tasks, select wrong targets or be influenced by third-party content. Even a formally valid action can
have unwanted consequences. Approval, visibility, selectors, local rules and tests reduce risks; they guarantee neither
freedom from errors nor the recovery of data that has already been changed.

The operator determines target computers, access, permitted activities and supervision. The operator needs the authority to
control the affected systems and data. Scope, target, irreversible effects and necessary backups must be checked before
administrative production workflows. A session approval applies only within the configured permission profile. A test
computer may expressly be authorized for unattended operation; this is no approval for third-party production systems.

New node policies use restricted semantic rules. The explicitly selected desktop profile and legacy policies allow broader
operation under the worker's OS rights. Administrator rights do not arise from an MCP request; the operating-system boundary
remains in effect. For broad administration the operator must deliberately provide this authority. Apple TCC, protected
Windows desktops and comparable OS boundaries must not be presented as automatically granted rights.

Third-party community cartridges are currently not approved for automatic public activation. Local macros are operator
drafts. Unknown content is checked in separate accounts or isolated test environments. Docker shares the kernel with the host;
unknown executable extensions require an appropriately separated VM.

The current public review subject is the C# core for linux-x64 with controlled X11/container runs. macOS, Windows, Android,
iOS and Wayland have their own acceptance boundaries. A cross-build is no device approval. Laya is an optional isolated IP
service; the Laya image reviewed here is not part of the approved core package.

These notes describe function, operating limits and known risks. They are no additional indemnity, do not change LICENSE.md
and leave mandatory statutory rights unaffected. Paid services and individually promised properties require suitable separate
contracts. No independent security audit or legal approval is claimed here.

## Review and responsibility in operation

The provider's review comprises documented tests and reviews to the best of its knowledge within the stated review scope.
It is no guarantee of freedom from errors, of every conceivable deployment environment or of freedom from damage.
The operator additionally checks the suitability for their task, the actual application version and their environment.
A local deployment approval applies only to this reviewed scope and must be reassessed after relevant changes.

Before every deployment the operator determines which data and settings can be affected. Before state-changing workflows,
sufficiently current, consistent backups protected against the operation and a verified recovery path must exist. This is
part of administration regardless of the tool used. A source-code backup does not replace a backup of the application
database. Acquiring the software alone does not include a backup service. Services expressly assumed by the provider remain
unaffected.

Source code can be inspected and compiled within the scope of its license. The operator can have their own review carried
out, also with an AI. Such a review names version/digest, scope, results and open limits. Forgoing a source review leaves
possible undetected problems open; it is no presumption of knowledge of concrete defects and no automatic waiver of rights.
A completed review makes the operator responsible for their deployment decision without attributing every hidden product
defect to them. The necessary review of the concrete workflow and its backups remains required independently of an optional
complete source review.

Concrete defects and security findings discovered during a review should be reported promptly and to a reasonable extent so
that they can be investigated and fixed. The report names the reviewed version and distinguishes confirmed findings from
unverified AI suggestions. Security findings and sensitive diagnostic information belong in the
[confidential reporting channel](../../SECURITY.md); fixing the problem yourself is no precondition for reporting.

In case of deviations or an unknown result, affected writing workflows must be stopped and investigated. Blind repetition
can duplicate changes that were already executed. Malfunctions are reported promptly with suitable, protected diagnostic
information. Whether omitted reasonable checks, backups or damage-mitigation measures reduce a claim for damages depends on
their actual contribution and on the statutory rules.

The [deployment review template](OPERATOR-REVIEW.md) supports the documentation. The
[license model](LICENSING.md) separates free private use, free non-production testing and licensed business production.
Additional contractual agreements are reserved to the respective license agreement; these operating notes establish no
automatic limitation of liability.

## Alpha and third-party scripts

This version is an alpha. Errors and incomplete functions are to be expected. The operator selects or creates scripts,
instructions and local macros and determines their execution rights. Own or third-party content can trigger unintended
deletions, costs, disclosure of data or loss of function. Merely passing on a third-party script does not mean that its
content originates from or was reviewed by k-lani-bot.

Responsibility for script content is distinct from defects of our executor, of the cartridges we supply and of packages
expressly offered as reviewed. This description is no blanket disclaimer of liability independent of jurisdiction. Whoever
wants to limit the reach of an automation technically uses appropriately separated OS accounts, permissions, workspaces and
sandbox/VM boundaries. A risk checkbox does not replace these boundaries.
