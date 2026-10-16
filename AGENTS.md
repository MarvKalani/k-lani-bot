# Start here

**Your role is to build the tools you will then use.** k-lani-bot turns your first screenshot-assisted operation of an
application into an executable, parameterized MCP function. When an authorized task has no suitable function, explore
only its necessary UI, write and verify the missing procedure, make it callable through the existing gateway, and use
that function yourself with fresh inputs. Repeating screenshot/click/reasoning rounds is the discovery phase, not the
finished product. Aim for one business-operation call; keep its deterministic steps and checks outside model context.
Separate authoring cost from reuse cost. Automatic capture helps with supported actions; it does not replace your
responsibility to author the complete procedure. Read [the worked authoring example](docs/AGENT-AUTHORING.md).

**Identify, qualify, then compose.** Establish the actual application variant/build and required access before authoring
or reuse. On a different build, check whether the task's selectors, actions and results still work; revalidate compatible
functions, adapt changed ones, or derive a cartridge for the new observed build. Never just change a version string to
make a check pass. Keep one cartridge per application variant/version, revisions inside it. Compose those qualified
application functions into the user's cross-program task; the caller should need its inputs and outcome, not each UI step.
Saved actions bind to application capabilities through a stable data/result contract. Replacing a mail application changes
its qualified cartridge and binding, not the cross-program action. During routine operation call the saved actions only;
return to screenshot/UI exploration when a failed result, missing case or version change requires a tested improvement.

**Back up before changing state.** First establish a scoped, verifiable backup and recovery path for the data and
configuration an operation can change. Read-only discovery may determine that scope first. A source snapshot is not an
application-data backup. Use existing qualified backup methods until the planned backup service is implemented; do not
invent its commands. Missing or inadequate backup evidence blocks the dependent mutation, not independent read-only work.

**Downloaded cartridges stay untrusted until accepted locally.** A publisher's validation label or signature does not
replace complete AI acceptance of the actual package and its dependencies. Only an authenticated administrator's explicit
decision can waive that AI acceptance for the exact known artifact and declared scope; record it as an exception, never
as an AI pass. Changed bytes, dependencies or scope need a new decision. This waiver does not waive backup, integrity,
technical compatibility or node/OS permissions. Keep this policy distinct from mechanisms not yet implemented.

**k-lani-bot is AI-assisted robotic process automation (RPA).** It connects real desktop and browser applications to
task-scoped MCP calls. During verified first use it can capture reusable actions; later calls should need less model
context than Computer Use. The operated node runs the application and enforces access. The AI/MCP client can run elsewhere.
The product is not a model provider, and using its MCP tools does not require buying a separate model API subscription.

**One MCP, multiple nodes.** `k-lani-bot` is the AI-facing MCP/controller; `k-lani-node` is the operated component.
"Gateway" names the controller's routing implementation, not another product. Both roles currently use the executable
`k-lani-bot-node`, with different startup options. Preserve working command paths until a tested executable migration.
Both the MCP/controller and the node are implemented and built for Windows, macOS and Linux. Do not describe the
controller as Linux-only or cross-platform support as limited to nodes; keep build evidence distinct from native acceptance.
Target architecture: any Windows/macOS/Linux controller operates a mixed fleet of Linux/macOS/Windows/Android/iOS nodes
through one MCP. Controller and target operating systems need not match. Do not describe the current mobile bridges as
completion of the intended independent mobile-node coverage.
Nodes can pair and connect back to the controller. Core/native-cartridge routing supports multiple registered nodes;
compact UI currently selects one default node and new registry entries require an MCP restart. Do not claim live discovery.
Windows/macOS/Linux have desktop backends; Android uses host ADB, and iOS is an experimental macOS simulator bridge.
The planned Git publication is source-only. Linux candidate binaries and Docker test images are separate private evidence,
not selected public downloads. Keep platform source and tests; do not turn the test host OS into a product requirement.

Before implementing, connecting or measuring:

1. For an implementation task, read its current task file and `.internal/docs/tasks/RUNBOOK.md` when present first.
   Read [the agent operations guide](docs/AGENT-OPERATIONS.md) when connecting or measuring; local code/unit-test work
   does not first require a live node, provider login or completed performance measurement.
2. Check Git status and the starting commit. If present, read `.internal/docs/tasks/README.md` and the assigned task's
   current status. Preserve completed work; historical instructions and process IDs are not current commands.
   A suggested next task does not override the user's current request. Do not recreate an implemented harness.
3. Identify the actual route: core node MCP, native program cartridge, compact UI gateway, or authentic Computer Use.
   Discover its current tools and schemas. Similar tool names across gateways do not guarantee identical arguments.
4. Reuse existing registered node access. Probe `list_nodes` / `get_machine_capabilities` on core MCP, `ui_info` on compact UI,
   or `cartridge_call` with `function: cartridge_node_status` on the native cartridge gateway. Its catalog can work while
   the node is disconnected. Missing tools in this chat do not establish that the node is offline. Never replace pairing state to repair
   a client configuration; initialize only a genuinely new node. Keep keys, registries and raw traces private.
5. Treat node access, model-client login and model-token telemetry as three separate prerequisites. Use existing authorized
   client/subscription access where available. Do not default to requesting API keys, installing a model client in Docker,
   or mounting the host home/credentials into a worker. A missing adapter is an implementation gap, not a missing subscription.
6. For measurements, read [the lab runbook](docs/performance-lab.md). Run no-model preflight first and report its infrastructure,
   application and model/baseline results separately. Exact tokens come from model-client usage; node bytes are not tokens.
   Use the same model/effort and verified task scope on both sides; keep authoring, first use and reuse separate.

At the start of work, state the concrete task, chosen target/route and next check. During operation name the application,
action and expected result, not just "read screen". When blocked, name the failed layer, observed evidence and smallest
missing implementation or input. Continue independent correctness work; do not turn unavailable measurement into a build gate.
When changing connection setup, gateway schemas or measurement adapters, update the operations guide and affected runbook
in the same change so that implemented routes do not remain documented as missing.

# Repository conventions

- Keep implementation small and easy to inspect in a debugger. Prefer named methods over nested, dense expressions.
- Use descriptive names. Do not shorten names merely to reduce text or tokens.
- Use one statement per line and ordinary multi-line blocks. Do not introduce compressed method bodies.
- Keep lines in `*.cs` at 140 characters or fewer, tabs and indentation included. `scripts/format.sh --check` enforces it.

## Code standard
These rules are the owner's definition of clean code. `tools/CodeStandardAnalyzer` fails the build on tuples (KLB001), on a
method that returns `bool` and throws (KLB002), on a production method that throws an exception its header does not name
(KLB003), on a switch over a project enum that does not name every value (KLB004), on a method with more than 40 lines of
code, blank and comment lines not counted (KLB005), and on a type without a comment above it (KLB006; nested types too, one
part of a partial type is enough); the other rules, such as what the comment says and its length, are checked in review.
A value that cannot occur ends in `throw new UnreachableException()`, which KLB002 and KLB003 do not count as a failure.
- Minimal, clear, precise. No text walls.
- Names say what a thing is or does. Variables, methods and constants are spelled out; no unexplained literal numbers.
- Every method follows input, processing, output: check the inputs, compute, return. Blank lines separate the three.
- Every type opens with its use case: who calls it, in which situation, in at most four lines. Standards are named with
  their edition or date, for example ISO 13616-1:2020.
- A method gets a one- or two-line header only when its name does not already say what it does and why.
- Failure is part of the signature. A method that can fail on ordinary input is a `TryX` returning `bool`, and a method
  returning `bool` never throws. Where an exception is unavoidable, the method header says so.
- Types, not strings: every finite set of values is an enum, every result with more than one part is a named record.
  Strings exist only at the boundary (protocol, files, OS) and are converted to types when read. No tuples.
- Methods stay short, about 40 lines; a longer flow is split into named steps.
- Comments explain why, never repeat the code, never mention tasks, tickets or people. Comments and identifiers are English.

## Repository rules
- When present, `.internal/docs/tasks/README.md` is the canonical local task index. Keep task status and evidence there;
  preserve completed task history, and never force-add `.internal` to Git. Public product docs are not a second task backlog.
- Write task instructions as executable handoffs: current status, first command, existing versus new files/functions,
  exact input/output behavior, ordered changes, test cases with expected outcomes, valid verification commands and a done criterion.
  Label proposed APIs and CLI suites as new work and assign their implementation to the task; never present them as existing commands.
  Keep historical instructions in linked archives. Separate code prerequisites from optional model-measurement prerequisites.
- Model-based performance measurements are explicitly requested, bounded Docker runs, not normal build or commit gates.
  Keep implementation correctness and measurement status separate; required deterministic repository checks still apply.
- Run `scripts/format.sh` before staging and `scripts/format.sh --check` before committing.
- Run `scripts/build.sh --check`; warnings are errors for every project. Do not add blanket warning suppressions.
- Commit hooks validate the staged snapshot; CI validates the committed source. Do not bypass the checks.
- A regression reports a failed check with its message and exit code 1. An exception left unhandled aborts the process
  and leaves a core dump of about 20 MB per red run. `scripts/check.sh` disables core dumps for its children; start a
  single regression by hand with `ulimit -c 0`, and give a new regression entry point a guard that returns 1.
- MCP/controller identity is `k-lani-bot`; the operated component is `k-lani-node`.
  The current executable/bundle identity remains `k-lani-bot-node`; use that actual name in working commands.
- Native status indicators and activity borders belong to the operated node. A controller must not instantiate desktop visibility hosts.
- Windows product windows use WPF and shared styles; use native Win32 tray integration rather than WinForms.
- One program variant and application version identifies one cartridge. Revisions update that cartridge and do not become extra catalog entries.
- Store the active cartridge directly under its application-version directory; keep its revision in the manifest, not another directory.
- Run GUI regression tests in a private virtual display/session, never on the operator's physical desktop. Its bus
  activates only the accessibility service; a host desktop service started there has no display and aborts with a core dump.
- Application cartridges try native APIs or object models, accessibility-tree identities with semantic actions, and documented
  keyboard commands before physical clicks. Coordinates are unreliable when windows move, layout changes, or another window
  covers the target. Use them only as an explicit last resort after the other routes were tried, record why, and verify the
  fresh target and result. Prefer an observed element ID to a point; never store fixed screen positions when an object is reachable.
- Keep the full cartridge inventory on the host, but expose only the task's topic and the tool descriptions needed for it.
  Discover topics through a compact index and load schemas on demand; do not send the full application's tool catalog or UI tree by default.
- Each program cartridge starts with Administration (configuration), Use (operating the application), Info (metadata/capabilities),
  and Programming when a programmable interface is available.
  Subdivide these by task and load only the selected branch; selecting Use must not load unrelated mail, calendar or account tools together.
- Start cartridge use with compact Info identifying the application variant, version/build, platform and required access capabilities.
  Assess compatibility per access route and function: tested UI selectors may require an exact build, while native APIs may use a
  tested version range plus non-mutating capability checks. VBA/COM access alone never establishes universal compatibility.
- Return requested fields, bounded pages and change summaries. Execute deterministic recipe steps locally and return a concise result;
  detailed evidence remains available by reference. Compact data must preserve the information needed to verify the outcome.
- Inbox sorting tries confirmed rules, a calibrated decision model, the selected AI reviewer, and finally the owner.
  Matching rules skip model calls; model confidence alone is not validated probability. Preserve financial and important-message
  exceptions, and leave unresolved messages in the inbox for personal review.
- Cartridge acceptance must compare measured model input/output tokens, image tokens, tool descriptions, results and retries against
  Computer Use for the same successfully completed task and information scope. Count authoring separately from routine execution;
  a cartridge that does not reduce routine token consumption has not met the product goal.
