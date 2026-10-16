# Private desktop performance lab

The local step efficiency display aggregates retained node diagnostics only; it does not supply model-client token
telemetry. The private Secret Service/backup regression in `scripts/check.sh` uses synthetic data and an empty isolated
keyring. It is a correctness check, not a token comparison or access to the operator's credentials.

For agent-built procedures, follow the [author/operator walkthrough](AGENT-AUTHORING.md). Exploration and cartridge
authoring are a separate phase. A reuse trial starts with the qualified saved action, fresh inputs and a fresh client
context; it should invoke that action rather than reconstruct its UI steps. Record outer MCP calls separately from
the internal node operations. The executable Tryton example is correctness evidence, not a new token-savings result.

`scripts/measure-performance.sh` keeps deterministic correctness checks separate from explicitly requested model measurements.
The default invocation prints help. Builds and commit hooks never start a paid profile.

## Choose the access path first

The model client stays on the host; only applications and the operated node belong in the isolated desktop.
Existing authorized subscription/client access is a valid starting point. A new API purchase is not a product requirement.
Read [Agent operations](AGENT-OPERATIONS.md) before changing node registration or model credentials.

Distinguish **using the node from the current client** from **automating a paired model comparison**. The first needs a
working MCP connection to the target; the second additionally needs run-scoped model telemetry and a compatible driver.
Missing measurement support does not imply missing node access.

For a comparison operated from the current chat, register the private lab route in that client and refresh its MCP
connection. Check the active tool list and `ui_info` after refresh; editing a server configuration does not retarget a
running MCP process. The native compact route requires the UI tools and cartridge routing tools for stored-function reuse.
A separate screenshot-only server exposes its image and physical-input tools; it must not expose mail data or the oracle.
Keep model selection in the requested client: record the exact model identifier, effort and run-scoped usage. A CLI run
with another model cannot stand in for the requested current-chat run. Local relay configuration and traces stay private.
Disposable labs may expire while the chat waits. Tool discovery should remain available; reconnect the relay to a fresh
lab and verify its live gateway against the captured descriptors before input. Forward screenshot content as images,
never as base64 text. An interrupted or incorrectly delivered image run cannot support a matched-task savings claim.

Current implementation limits:

- Preparation creates an accepted image and probes applications; it does not attach the current chat to a live MCP node.
- The worker starts Xvfb/D-Bus. `scripts/remote-lab.sh` now manages a private node/supervisor lifecycle for the existing client.
- The automated host adapter contract below is implemented, but a production model-client adapter is not bundled.
- `scripts/performance/adapters.py` currently requires credential environment variables and creates an isolated HOME.
  It cannot automatically inherit a managed subscription login. That adapter path needs implementation, not a fabricated key.
- The earlier browser pilot has a usage collector in `modules/efficiency-benchmark/model-runner.mjs`; its protocol and
  browser tool availability do not automatically satisfy this desktop runner or prove native Computer Use access.

For a request to connect an existing client, reuse its authorized access through the remote bridge below.
Do not install the model client or copy its credentials into the worker. Keep tool-byte measurements separate from actual
model-token counters. If counters are unavailable, operation/correctness can proceed while the token comparison stays pending.

## Remote control from the existing client

```bash
scripts/remote-lab.sh start
scripts/remote-lab.sh status
scripts/remote-lab.sh stop
```

Default private state is `.artifacts/performance/remote-lab`; select an existing lab with `--directory <ignored-lab-directory>`.
Start reuses the accepted local image and the existing node state. It never builds/pulls an image, resets keys or starts a model.
A running instance is not silently rebound. Startup has a 20-second deadline. The host supervisor holds TLS/registry credentials;
the non-root sandbox gets only the bounded synthetic workspace and readonly worker policy, with no provider credentials.
The product sandbox route uses the host's non-root UID for the mounted policy/workspace, while scored runner workers use UID 10001.
The corrected entrypoint accepts native worker arguments and keeps all D-Bus/accessibility diagnostics on stderr.
The image includes the documented `wmctrl`, `xclip` and `scrot` helpers for actual node operations.

Start returns the private `gateway.json` path. Register its executable/arguments as core MCP, or use the documented compact
frontend with that file. When Node.js is available, start also creates `compact-meter.json` for the existing transparent meter:
register `node modules/efficiency-benchmark/mcp-meter.mjs <absolute-private-compact-meter.json>` to expose only
`ui_info`, `ui_observe` and `ui_act` and retain model-facing schema/result bytes, calls, images and timings privately. Use a persistent MCP session for observation/input/verification. A diagnostic `remote-lab.sh call`
reads one `{action,args}` JSON request from stdin and closes its session afterward; it never replays a mutation.
Callers must supply the native schema's operation ID for mutations. Ending that transient session invalidates its cached references.
When testing result-detail discovery, add the three cartridge routing tools to the private meter's selection.
The compact server offers them without learning configured; `Info.result` is the only learning-independent Info function.

To delimit current-client usage without an extra model process:

```bash
scripts/measure-chat-usage.sh begin unique-run-name
# Operate the private application through its selected MCP tools.
scripts/measure-chat-usage.sh finish unique-run-name
```

The importer selects `CODEX_THREAD_ID` by default; `--thread` and `--sessions` select an explicitly authorized usage source.
Only reported `event_msg/token_count` counters are used. It does not infer tokens from files, results or images. Missing/reset
counters stay null; reasoning remains inside output tokens. Markers cannot be overwritten or mixed between threads.
An unfinished/final request may not yet be reported, so this interval export is always flagged incomplete/inconclusive.
It includes the existing chat's context cost; use complete equivalent client contexts for any accepted routine comparison.
Node tool bytes/calls/timings and independently verified results are separate evidence. No subscription charge or savings is inferred.

Native compact compatibility preflight requests only the selected function's `action_schema_names` through the existing
registered HTTPS node client. The node returns its actual permitted `actionSchemas`; unavailable or older-node evidence
remains unknown. This is a read-only correctness probe, not a model run or token measurement. The EXE's native compact
CLI/MCP route supports Info/Observe/Act, structural scans and verified Capture/Reuse through the existing HTTPS connection.
The native profile also routes bounded dynamic form reads/options, exact-context writes and read-only validation without
a learning root. Configuring a private learning root additionally enables automatic first-use capture during `ui_act`;
count that authoring phase separately from routine reuse. Verified installed definitions are not announced by name; they are
browsed under `Use/UI`, and capture/storage failures never replay completed input. Linux probes an actual missing AT-SPI text interface before
input preparation; only its explicit not-dispatched refusal can qualify semantic focus/keyboard capture. Full output equality and explicit unknown constraints precede
response clipping. The native profile additionally routes workflow Run/Status/Resume and verified Begin/Finish/Capture,
plus observed read capture. A private `workflow` section in version-1 `--ui-config` supplies separate job state and trusted
authority. Resume reconciles original operations and actual complete outputs, never replays unknown input. The separate
Node.js frontend remains the development parity reference. Native `Info.compatibility` assesses one selected function without
input; `Programming.revalidate` consumes existing matching signed isolated evidence configured by private `--ui-config`.
It never creates signing keys or accepts caller approval/result references as functional evidence. Issuer and reader
must use matching frontend fingerprints; the JS issuer cannot be assumed to qualify the native frontend.
The separate `docker/runtime` target holds only the published node executable, its desktop/accessibility libraries and the
installed application; preparation verifies that `dotnet`, `node`, `nodejs`, `python` and `python3` are absent.
`scripts/measure-performance.sh --prepare --suite native-runtime` builds it from the frozen source and runs one actual
Mousepad lifecycle without a model: form scan, verified first use, a saved file read independently of the application,
controller restart and reuse with a fresh value. Only then it writes its own private `native-runtime.json` lock, next to
and never in place of the desktop image lock. This is runtime qualification, not a token measurement; a model profile
remains a separate, explicitly requested run.
Linux window identity uses actual executable-to-package ownership and installed package versions. It refuses to infer
builds from process labels or private copies. This metadata improvement is a correctness check; it does not establish
installed-application acceptance, runtime qualification or token savings.

Both native gateway profiles discover one catalog entry for a native bundle plus its generated extension at the same
variant/build. Legacy generated paths are resolved from stored identity and retained on update. This catalog correctness
behavior does not establish token savings or qualify a missing runtime image/application environment.

## Profiles

`compatibility` covers unchanged contracts, a declared compatible patch label and an incompatible writer action.
The builds are explicitly synthetic metadata fixtures; actual application operations/results still come from the installed
native GTK delivery application. The host adapter receives `compatibilityFixture` and `shared_fixture_proxy` setup data.
Use the shared `compatibility-core.mjs` proxy from the candidate tool snapshot for both frontend arms, with a private
`isolation: synthetic_private_lab` configuration; do not also enable a second fixture wrapper in the frontend.
The negative case requires an actual serialized incompatible status plus independently unchanged live state and no saved output.
It is `business_task: false` and contributes no business savings ratio. An older arm that cannot finish stays unmatched;
its short abort is not a successful comparison. Quick retains six runs/600 seconds/90 seconds per run and no retries.
This suite requires `--route learned-mcp --comparison before-after`; other paid routes are rejected before trials.
See [compatibility operations](compatibility.md) for contract, signed isolated receipts, v1 migration and revision recovery.

`staged-workflows` uses three delivery authoring/reuse phases: Delivery, Express, a fresh address, order Apply,
modal confirmation, and verification of the application's saved JSON. The host oracle reads actual saved output;
it does not expose expected answers to application tools. L2/L3 reuse exported, validated definitions on new app instances.
The reusable library validator accepts staged definitions in the same application/version directory and rejects
runtime IDs, coordinates, expressions and literal training values. Authoring and reuse stay separate phases.
The quick ceilings remain 600 seconds, six model runs and 90 seconds per run; no automatic extra attempts.

For `learned-mcp`, the host adapter receives `learning` for the controlled delivery fixture and a `workflow_setup`
template with separate job/cartridge roots and `authority_source: authorized_node_principal_connection`.
The reviewed adapter must remove that marker and supply actual trusted `authority: {node, principal, connection}`
to the gateway's `workflow` configuration after its connection probe. The template is not proof of login or node access.
Older baseline gateways may lack this route; they complete the same application task through existing tools.
No compatible production host adapter is bundled. See [workflow operations and recovery](workflows.md).

Native correctness can reuse a pinned desktop with current readonly node/fixture binary overlays. Such an overlay
is explicitly labelled correctness evidence and does not accept an updated production measurement image.
Scored profiles still require the prepared image for the exact worker source, authorized adapter and private budget.

`result-contract` uses three focused cases: write/save a large known literal in Mousepad, read its complete unknown Unicode
document, and reject invalid result options without changing the document. The existing `desktop-core` cases are separate.
The host oracle independently reads the live editor through AT-SPI after the model stops, checks unchanged inputs and
actual saved content, and requires the full final answer for the read.
The gateway also checks the full native field value immediately after every text write, including routine generated calls.
Appending instead of replacing, an empty value for a nonempty request, or a matching clipped prefix cannot count as success.
The negative case also needs the actual serialized gateway error in the private adapter event stream;
a final-answer claim alone does not establish refusal. It is labelled
`business_task: false`. No expected values or oracle files are mounted in the worker, and the probe performs no input.
These host contracts have deterministic tests; a model comparison still needs an authorized compatible adapter and explicit budget.

`state-discovery` adds two real Mousepad document windows for A/B/A comparisons and a separate native synthetic discovery fixture
with 250 editable controls. Its prepared, parameterized catalog has 120 functions in one program/version directory;
function discovery is part of the task, and no selected-function answer is placed in the prompt. The third case requires
complete unique control/function inventories and an actual verified write/save. The host independently reads live accessibility
values, persisted application output and unchanged input hashes. The changed-document case saves through the application.
The catalog is fixture setup, not model authoring, and contains no literal write answers, native IDs or coordinates.
The new fixture requires an image prepared from its worker source; an older accepted image is not silently relabelled compatible.
Model profiles still require their reviewed adapter and explicit private budget. Unnamed controls follow the node's text-write rule
for plain text entries; every other unnamed control is refused.

`dynamic-forms` uses the separate native synthetic door configurator, with Basic/Wide widths, dependent side panels and required
left glass. It includes Basic/900/no-glass, Wide/1200/both/security and Basic/1000/left/frosted. The operator discovers actual
fields/options, validates requirements and saves through the application; the independent host oracle checks actual JSON output
against the requested valid dimensions, panels and glass. No verifier expectations are mounted in the worker.
The `door-form <result-path>` lab command starts that controlled program variant. Its field descriptions explicitly publish
versioned constraints/dependencies; its real GTK children supply option lists. The original Local/Delivery fixture remains separate.

`modules/desktop-ui-mcp/form-regression.mjs` is an explicit no-model private-session acceptance driver, taking a mode-600 JSON
configuration with `nodeExecutable`/`fixtureExecutable` and a new output directory inside `.artifacts`. It requires installed
Openbox/wmctrl/xdotool and the private GUI wrapper. Missing host helpers fail before node initialization; use an isolated prepared
desktop instead. Correctness tests can mount the current built node/fixture read-only into an existing pinned desktop image;
record this as a binary-overlay test, not as acceptance of a new production image or a scored performance treatment.
An updated accepted image remains a prerequisite for a requested model profile. No image is rebuilt on each ordinary edit.

| Profile | Deadline, including startup, verification and cleanup | Model runs | Per model run |
| --- | --- | --- | --- |
| smoke | 120 seconds | 0 | — |
| quick | 600 seconds | 6 | 90 seconds |
| matrix | 1200 seconds | 12 | 90 seconds |

A quick profile runs three matched cases in counterbalanced order: baseline/candidate, candidate/baseline, baseline/candidate.
Each model invocation has a new context. There are no automatic retries, effort changes or transitions to a larger profile.
Timeouts and missing prerequisites return exit code 3 and retain a partial report. Missing usage is `null`, never zero.

## Preparation and preflight

```bash
scripts/measure-performance.sh --prepare --suite desktop-core --max-setup-seconds 1200
scripts/measure-performance.sh --preflight --suite desktop-core
scripts/measure-performance.sh --profile smoke --suite result-contract
```

Preparation freezes an allowlisted source archive, builds the dedicated Docker image and runs real application probes.
The signed Ubuntu archive is fixed to `20261007T000000Z`; SDK and base image references are pinned by digest in
`docker/performance/dependencies.lock.json`. The accepted private image lock records all installed package versions,
application versions, executable hashes, image digest and capability results. A build alone does not accept an image.
Measured runs require the exact accepted runtime source and image; they never download or build an image.
Host coordinator changes can reuse an unchanged worker image, while every measurement still fingerprints the full candidate source.
A candidate edit during measurement invalidates its report. The independent verifier has its own recorded source fingerprint.

The probes use Converseen to persist WebP, Xarchiver to create ZIP, Mousepad to expose text through accessibility,
Poppler to read PDF text and LibreOffice Calc's native UNO interface to save XLSX. The host oracle then reads the actual
output files and verifies the saved A1 cell. The initial WebP capability checks encoding, dimensions and alpha; it does
not assert a lossless conversion profile. The image workload separately requires exact decoded RGBA, with RGB under
fully transparent pixels canonicalized to zero. A failing codec, application or accessibility route stays unavailable.

The preflight reports infrastructure, application capabilities and provider/baseline availability separately.
Missing provider configuration does not invalidate passed correctness tests or application probes.
With no configured host adapter, an overall `unavailable` and exit code 3 are expected even when infrastructure and
applications pass. Read the individual checks in the returned private `report.json`; do not rebuild a passed image or
re-pair a working node to fix a model/baseline failure. `--profile smoke` tests deterministic contracts only.

## Worker isolation and synthetic fixtures

Workers run as UID 10001 with a read-only image, dropped capabilities, private IPC, private Xvfb/D-Bus sessions,
fixed locale/fonts/1280×720 viewport, fresh profiles and bounded tmpfs storage. They have no external network, host desktop,
host home, provider credentials or Docker socket. Only generated synthetic inputs are mounted, read-only.
The independent oracle and its expected values remain on the host, outside application tools and worker mounts.
Every trial creates and removes its own container. Provider orchestration runs outside that container.

The native cartridge lab also supplies a labelled synthetic dynamic form:

```bash
k-lani-bot-cartridge-lab dynamic-form /workspace/result.json
```

Its option widget remains the same while options change; its address field disappears and reappears. Duplicate Apply
labels have distinct named contexts. The application alone persists the result after its modal confirmation.
Deterministic tests exercise stale revisions, unavailable choices, hidden fields, cleared required input, duplicate labels
and background actions behind the dialog. Native regression commands always use `scripts/private-gui-session.sh`.
The desktop component cases are one read, one write and a changed conditional form; they expose fixed operator overhead.

Learned routes persist only validated generated definitions and compatibility metadata between L1/L2/L3.
They keep one `programs/<variant>/<version>/generated-ui.json` with its revision inside the document.
Fresh workers receive these through a non-root archive stream. Runtime IDs, coordinates, literal write values and unrecognized
state are rejected; fresh target/type checks and output verification remain the existing gateway's responsibility.
An initial empty library is not replaced with a prewritten function-name answer.

## Host adapter contract

A reviewed host model client is configured privately with `--provider-config`. The supplied
`docker/performance/provider.example.json` is an interface example, not an authorized provider setup or run budget.
Its `command` must implement the JSON-lines protocol below. Pointing it directly at a model CLI is insufficient.
Copy and review it in an ignored directory, use mode 600, supply only explicitly authorized credential environment variables,
and set token, provider-call and reference-cost limits. No credentials belong in the JSON or worker.

One executable, client version, shared prompt, model and effort drive both arms of a quick comparison.
The client receives a JSON request on stdin and writes JSON-lines events on stdout; stderr and raw events stay private.
A preflight request has `mode: preflight` and `paid_calls_allowed: false`. Its response must confirm `available: true`
and the exact configured provenance. Actual tool schema fingerprints, provider/tool versions and the complete available
action set must come from the installed provider adapter, not a replacement screenshot tool.
Unsupported model/effort/tool combinations are rejected before trials.
Authentic Computer Use requires `implementation: authentic_provider`; a custom screenshot adapter cannot use that identity.
No authentic provider adapter is bundled or inferred from a Codex account login.

Run requests include worker identity, compact application build/access information, shared prompt, model, effort,
source fingerprint, immutable host tool-source directory, phase, optional cartridge root and `retry_count: 0`. The model gets only worker application tools,
with topic/schema discovery on demand. It must not get a host shell, verifier access or helpers that choose actions or manufacture outputs.
The reviewed host client must enforce this tool boundary, load gateway code from the supplied immutable tool-source directory,
and authenticate the provider independently.

| Event | Meaning |
| --- | --- |
| `provenance` with `value` | Exact preflight-confirmed route provenance |
| `request` with `reservation.tokens` and `reservation.reference_cost` | Reserve the maximum next provider request; wait for `{"authorized":true}` on stdin |
| `usage` | One new `request_id`, actual provider `input_tokens`, `output_tokens` and reference cost; optional cached/reasoning/image counts |
| `tool_descriptions`, `tool_result` with `serialized` | Record the exact UTF-8 payload supplied by the shared client, separately from provider tokens |
| `metrics` | Measured image bytes/count, native calls and interventions; never derive exact image tokens from bytes |
| `answer` with text `value` | Final model observation for the read control; this is not an application-generated artifact |
| `completed` with `completed` | Client completion claim; independent verification still decides correctness |

Only one provider request may be outstanding. Missing usage blocks another request. Reservations cannot exceed the remaining
private budget. Every accepted usage event is flushed immediately, including failed attempts. Input/output totals include
failed and interrupted attempts; supplied reasoning tokens are a subset of output, not an extra charge. Unknown breakdowns
remain unknown. The request/response protocol is a contract for trusted, reviewed provider clients, not a network sandbox for arbitrary executables.
Raw adapter and command traces have an 8 MiB cap; each exported worker directory has a 16 MiB archive limit.
Unreported tool/image/native counters remain unknown and prevent a complete scored result. Outputs and traces stay under `.artifacts/performance` and are never committed.

```bash
scripts/measure-performance.sh --profile quick --comparison computer-use \
  --ticket 33 --suite desktop-core --candidate working-tree \
  --model SUPPORTED_MODEL --reasoning SUPPORTED_EFFORT --seeds 11,22,33 \
  --provider-config .artifacts/performance/private-provider.json
```

For before/after comparisons, use `--comparison before-after --baseline EXACT_STARTING_COMMIT` and prepare that commit's
runtime separately with `--prepare --candidate EXACT_STARTING_COMMIT`. Package builds must match across the pair.
The harness's creation has no predecessor harness and provides no token-savings claim.
The generic matrix interface requires six explicitly selected configurations in the private `matrix` array and runs two
component cases each. It does not implement or automatically start application-specific model qualification.
The Thunderbird receipt workload is separate from infrastructure/transport preparation; it cannot silently fall back to a form.

## Optional mail image

```bash
scripts/measure-performance.sh --prepare --suite mail \
  --greenmail-image 'greenmail/standalone@sha256:EXACT_REVIEWED_IMAGE_DIGEST'
```

The mail target adds real Thunderbird, Evince and OCR tools. A pinned GreenMail service runs only on a fresh internal Docker
network with no published ports, using invented `lab.test` accounts. Preparation tests actual SMTP delivery and IMAP retrieval
and records the service digest. Missing GreenMail preparation stays unavailable. No external mail service is used.
Linux results do not establish native Windows, macOS, Outlook or Microsoft Excel compatibility.

Publish only explicitly reviewed, sanitized fingerprints/status summaries. Private traces, mailbox state, credentials and
oracle answers remain ignored. Correctness status and measurement status are reported independently; unavailable comparisons
never establish performance savings.
