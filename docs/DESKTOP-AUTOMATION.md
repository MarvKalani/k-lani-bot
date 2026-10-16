# Desktop Automation with MCP Cartridges

k-lani-bot gives the controlling AI programmable hands. The agent investigates an application through the existing
screenshot/UI controls, authors and verifies its missing capabilities, then uses the saved MCP functions itself.
The bot performs known steps locally and returns the checked business result.
This guide explains the design. Some parts are available today; others are planned, as listed below.
Start with the [executable authoring example](AGENT-AUTHORING.md).

## What is a cartridge

A cartridge describes how to operate one application variant and version. Updates revise that cartridge.
Forms, pages and tasks stay inside it.

A saved cross-program action combines capabilities through their data/result contracts. Replacing a mail program
changes its qualified cartridge and binding; the action can stay the same when the replacement satisfies that contract.
On an unfamiliar build, the agent checks the needed behavior, revalidates or adapts its implementation, and saves the
qualified revision or derived cartridge. Routine operation uses these saved actions; UI exploration returns for repair.

Each cartridge has **Info** for compatibility, **Use** for tasks, **Administration** for settings,
and **Programming** when the application provides a programmable interface.
The assistant loads only the topic it needs, such as reading mail or editing a workbook.

MCP (Model Context Protocol) carries function calls and results as JSON messages. The k-lani-bot MCP/controller and
k-lani-node component currently share a C#/.NET executable named `k-lani-bot-node`. The native compact profile is part of
that executable; JavaScript remains an optional development/parity implementation. Saved definitions use JSON to describe parameters,
targets, steps and checks. The assistant reuses a function by name instead of sending its code again.

One MCP connection can route to several registered nodes through the core or native cartridge profile. Nodes operate
their own desktop or a configured mobile bridge. The [operations guide](AGENT-OPERATIONS.md#2-connect-to-the-correct-node)
explains registration, reverse connections and the current compact-profile/registry-refresh limits.

## Discover first and verify every action

The scanner records reachable controls, their identities, types and current properties.
Values are read on request. Required fields, allowed values and button effects stay unknown until supported by evidence.
Recognizing a button does not prove that its action works.

A reusable function needs parameters, valid targets and a result check.
Before input, the bot checks the application, target identity, type and current conditions.
It then performs the action and checks what actually changed.
New controls that appear after an action are checked in the next stage.
Unclear results stop the workflow; writes are not repeated automatically.

Preferred access routes, in order:

1. Native APIs or application object models.
2. Accessibility elements and their supported actions.
3. Documented keyboard commands.
4. Coordinate clicks as a last resort, with a fresh target check and result verification.

Compatibility is checked per route and function. UI selectors may depend on an exact build or language.
An API needs a tested version range and capability checks; COM or VBA alone does not guarantee compatibility.

## Handle forms that change

In a door configurator, the selected model may change available sizes and side panels.
Left-side glass can be selected only when a left side panel is available.
A field can keep the same name and type while its allowed options change.

The bot tracks both the form structure and the current configuration.
After a selection, it refreshes affected options and checks whether earlier choices are still valid.
Old options and state references expire when their context changes.
Unknown dependencies require fresh checks. Labels or model confidence do not establish business rules.

Known choices can run locally. Missing preferences, unavailable values or unclear effects become explicit stopping points.

## Connect several applications

A workflow combines functions from separate cartridges. Each application keeps its own version and compatibility checks.
Once the workflow is verified, the assistant loads its contract; the bot resolves the individual functions locally.

For example, a planned Outlook-to-Excel workflow would:

1. Identify an email and its PDF attachment.
2. Read the PDF locally, using OCR when needed.
3. Extract the requested value with its meaning, currency or unit, and source location.
4. Check the exact workbook, sheet and destination.
5. Write and save the value, then read back the saved result.

That workflow must retain source references and completed steps to prevent duplicate entries after interruptions.
An open workbook must use its bound application session. Supported file formats, existing content and concurrent edits must be checked.
Document contents cannot change the task instructions or destination.
Files on different computers use explicit transfers and source checks.

## Keep model context small

Use the fixed gateway functions: `cartridge_info`, `cartridge_browse` and `cartridge_call`.
Keep the full inventory on the host and load individual function contracts when needed.

Return requested fields, bounded option lists, relevant changes and verified outcomes.
Keep documents, UI trees and detailed logs local where possible; provide retrievable references for further inspection.
Send the assistant a focused excerpt when a decision needs document context.
Incomplete lists, errors and unverified results must remain explicit.

The compact gateway offers optional `result_mode` on UI actions, generated functions and form scans.
`summary` confirms a fully checked known value without echoing it. `values` returns requested outputs in bounded pages;
`debug` supplies a bounded inline response or a detail index. Omission keeps the legacy response.
`Info.result` retrieves complete private details by reference, including when learning is not configured.
References expire and belong to one node/gateway session. See the [result contract](AGENT-OPERATIONS.md#choose-the-result-information-needed).

Application views retain separate bounded baselines and page cursors. Returning to an unchanged application can produce an empty
delta after a fresh read. Browse all pages for complete discovery; a changed view/library invalidates its cursor.
Automation IDs plus roles and semantic container paths tolerate display-caption changes, while input still requires a fresh unique target.

## Build and measure

Identify the application and access route, scan the needed views, demonstrate actions in a private test session,
and record their checks and dependencies. Reuse the functions after a restart with different inputs.

Compare the same completed task and information scope using the same model and reasoning effort.
Measure initial discovery separately from the second and third runs using the saved cartridge.
Include failed attempts, retries, cache use, tool descriptions, results and image usage.
Missing measurements stay unknown. A cartridge meets the token goal only when routine execution uses fewer model tokens.
Desktop workflows need their own tests on the target platform.

## Current status

Available: `Info.scan_form`, `Info.form`, `Info.result`, compact result modes, bounded per-application views/paging,
`Use/Forms` read/options/set/validate with Linux native metadata and semantic choices, automatic functions for verified text writes and tab navigation,
plus target preflight and output checks.

Planned: broader platform/provider coverage for field constraints, staged workflows, Outlook attachment extraction,
and the complete PDF-to-Excel workflow.

In the small browser extraction pilot, Luna low and medium each produced three correct inventories.
The richer inventory used about 5.6% more tokens than the existing compact UI view.
This does not establish desktop workflow savings or reliable operation at low effort.

Updated 8 October 2026.

## References

- [Detailed design in German](DYNAMIC-FORM-CARTRIDGES.md)
- [Form scanner](../modules/desktop-ui-mcp/form-scanner.mjs)
- [Extraction measurements](../benchmarks/form-scanner-20261007/measurements.json)
- [Outlook coverage](../cartridges/programs/outlook-classic/16.0.14334.20918/coverage.json)
