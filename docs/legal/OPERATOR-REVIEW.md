# Operator deployment review

This template supports the technical approval of a concrete action. It is no contract, no waiver of liability and no
blanket security certificate. An AI review can support the review; the operator is responsible for the approval.

| To document | Content |
| --- | --- |
| Responsible party and time | Responsible person/organization and date of the decision |
| Software version | Version, source commit and digest of the program actually deployed |
| Origin and build | Source of supply, own build where applicable and tools used |
| Target system | Computer, application, actual build and relevant environment |
| Action and authorization | Permitted task, data scope and required rights |
| Cartridge | Identity, revision, file digest, dependencies and local acceptance |
| Review method | Code/AI review and concrete functional tests; for an AI review, the model/tool and review scope |
| Result verification | Evidence that the expected change was completed in full on the correct target |
| Negative and abort cases | Rejected inputs, partial failures, stop and handling of an unknown result |
| Backup | Suitable, consistent backup and verified recovery path |
| Limits | Unreviewed functions, known restrictions and necessary supervision |
| Findings and feedback | Concrete errors, review status and reported notes; reference of the report where applicable |
| Decision | Approved scope or justified non-approval; conditions for a renewed review |

Before every deployment, check whether target, software version, permissions and backup still match the approval. Changes
require a renewed review of the affected scope. Missing review results remain open. "Compiles successfully" or "the AI found
nothing unusual" must not count as a review of all functions, dependencies and deployment conditions.

## Reporting findings

Whoever discovers concrete errors, security problems or significant ambiguities during a review is asked to report them
promptly and to a reasonable extent to **support@kalanis.de** so that the product can be improved. A helpful report names
the reviewed version, the affected function or code location, expected and observed behavior and reproduction steps that
are as small as possible. Unconfirmed AI suggestions are to be labeled as such; a working fix is no precondition for a report.

Security findings are reported [confidentially](../../SECURITY.md). Credentials, personal data, third-party confidential
code and unfiltered review transcripts do not belong in public reports. Share only necessary information that may lawfully
be passed on. The review is no obligation to find or fix every error yourself.

The template need not be completed again for every unchanged MCP call. A reused approval does, however, need a traceable
binding to the current version and the action scope actually permitted.
The [operating notes](OPERATOR-SAFETY.md) describe the technical and organizational requirements for deployment.
Additional contractual agreements are reserved to the respective license agreement.
