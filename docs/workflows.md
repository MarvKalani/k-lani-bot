# Verified staged workflows

The compact host gateway runs ordered references to verified cartridge functions. It checks targets before each stage,
so a later dialog need not exist before the first stage. Node scopes, fresh target/type checks and result verification
remain active for every native operation. The native EXE compact profile and the separate JavaScript reference provide this route.

Text replacement must match the complete fresh field value before a later action runs. Appended text, an unexpectedly
empty field or a matching clipped prefix cannot confirm success. An empty value is accepted only for an explicitly requested clear.
Before a later action, still-present written fields are checked again. Resume checks the final replacements as well as
the declared outputs; an `Exists` check cannot hide an incorrect value. A field removed by a later action requires a
checkpoint showing that the later step began after the setter's full verification. An incomplete keyboard sequence stays unconfirmed.

## Configure trusted access

Add `workflow` to the private gateway configuration. `stateRoot` and `cartridgeRoot` must be separate absolute directories.
The host supplies `authority.node`, `authority.principal` and `authority.connection` from its authorized connection.
They are configuration, never model arguments. The node ID must match the fresh capability probe.
Configure each application's existing learning cartridge in `applications`:

```json
{
  "workflow": {
    "stateRoot": "/absolute/private/jobs",
    "cartridgeRoot": "/absolute/private/cartridges",
    "authority": {
      "node": "observed-node-id",
      "principal": "authorized-principal",
      "connection": "existing-authorized-connection"
    },
    "applications": [{
      "cartridgeRoot": "/absolute/private/cartridges",
      "application": {
        "key": "delivery-fixture",
        "version": "1.1",
        "identity_source": "controlled-test-fixture"
      },
      "auto_capture": false
    }]
  }
}
```

This is a configuration template, not a new pairing command. Reuse registered node access. Jobs contain private
parameters, original operation arguments and evidence; keep them outside Git and published cartridge directories.

For the native profile, add `workflow` to the owner-only version-1 file selected by `--ui-config`:

```json
{
  "version": 1,
  "workflow": {
    "stateRoot": "/absolute/private/jobs",
    "authority": {
      "node": "ACTUAL_AUTHORIZED_NODE_ID",
      "principal": "AUTHORIZED_PRINCIPAL",
      "connection": "EXISTING_AUTHORIZED_CONNECTION"
    }
  }
}
```

Use the existing `--cartridges` library; native function lookups resolve its active generated extension per application/build.
Do not copy the JavaScript `applications`/`cartridgeRoot` fields into this native configuration. Both private config sections
`workflow` and `compatibility` may coexist. Startup validates paths/version/bounds before connecting and creates no jobs.
Run/resume require `ui_info` from the configured actual node ID, not its registry nickname. Status reads private jobs
without operating or contacting the application. New definitions additionally require the separate learning root;
existing workflows can run with a configured job root and no learning root.

Native definitions live in one `workflows.json` beside the active `generated-ui.json`, preserving legacy paths.
A private exclusive writer lock, byte bound and atomic replacement protect capture; duplicate workflow keys, unsupported
formats, literal bindings and unsafe paths are refused without overwriting the active definition. A captured function
becomes a stage only after its verified installation. Capture warnings never authorize replay of completed input.
Browse `Use/Workflows` for workflow keys, revisions and stage counts, without receiving every child schema or recipe.
Detailed progress/outputs/original operation evidence remain private and page through `Info.result`.

## Capture and discover

Start with fresh `ui_info`/`cartridge_info`. Through `cartridge_call`, begin `Programming.workflow_begin` with
`key`, `revision`, `inputs` and application aliases. Demonstrate and verify functions separately at navigation/dialog
boundaries. `Programming.workflow_finish` accepts explicit per-stage `bindings`, `decisions` and named `outputs`.
Alternatively, `Programming.workflow_capture` validates a complete definition referencing existing verified functions.
`Programming.learn_read` accepts `pid`, optional `window` (the handle of the window the reference belongs to), `name`, `topic`,
observed `ref`, `field` (`name` or `value`) and `output`.
Its source must currently exist and be nonempty; the reusable definition stores the selector, not the value.

A definition uses format `k-lani-bot-workflow/1`, a key/revision, typed inputs, application aliases, ordered stages and outputs.
Each stage identifies `function_ref: {application, name}` and requires
`preconditions: "fresh_unique_enabled_targets"`, `postconditions: "recorded_function_outputs"`.
Bindings have exactly one of these forms:

```json
{"address": {"input": "address"}}
```

```json
{"text": {"stage": "read_source", "output": "text"}}
```

No expressions, constants, forward references, runtime PIDs or screen coordinates are supported. Binding types must
match the function schema; an enum can supply a string parameter. Inputs support bounded string, number, boolean,
enum and opaque document values `{ref, sha256, origin}`. A document reference grants no filesystem access and does
not turn a string-only UI function into a document processor. A stage decision names a declared boolean input and prompt.

Definitions stay under `programs/<application>/<version>/workflows.json`, with revision in the definition.
The catalog holds at most 100 workflows and each definition has at most 32 stages. Browse `Use/Workflows` for compact
names/status metadata, then browse a selected function for its schema. Capture functions are under `Programming/Workflows`.

## Run, inspect and resume

Call `workflow.run` with `workflow_key`, `revision`, `parameters`, and a bounded `client_request_id`.
The result contains opaque `job_ref`, `status`, job `revision`, `completed_stages`, `next_stage` and `evidence_ref`.
The same request ID/content returns the same job; changed content under that ID is rejected.
Use `workflow.status` to inspect a job, and `workflow.resume` with its latest `expected_revision` and optional `answers`.
Answers can supply missing inputs or confirm an unused declined decision; they cannot replace already used values.
Detailed evidence uses the existing bounded `Info.result` store.

| Status | Meaning |
| --- | --- |
| running | Stages are advancing locally |
| awaiting_input | A declared input or explicit approval is missing |
| blocked | Current authority, scope, application instance or target prevents input |
| unconfirmed | A previous mutation's effect cannot safely be established |
| failed | A declared contract or execution prerequisite failed |
| completed | All stages and their current postconditions were verified |

Private jobs use atomic JSON replacement, file/directory fsync and mode-600 files. At most 100 jobs are retained;
full storage refuses creation without deleting jobs. Concurrent calls within one gateway serialize per job and check
the revision inside the lock. Another live gateway's lock is refused; Linux recovery removes only a proven dead/replaced
lock owner. Other operating systems conservatively refuse uncertain abandoned locks.

Before sending each mutation, the gateway durably records a UUIDv7 intent and passes that same ID to the existing
node OperationLedger. Resume checks ledger outcomes and fresh application state before completing a stage.
Missing/expired ledger entries, partial stage prefixes or unverifiable effects remain `unconfirmed`; no new retry ID
is generated. Resume also rejects a replaced application instance or changed function contract. A new job may select
a new instance after fresh Info. Read-only binding sources are reread and their digests checked before target input.
No automatic rollback is promised.

## Acceptance boundaries

Deterministic acceptance covers the native GTK delivery fixture and real installed Mousepad in a private desktop.
A control without a name or automation ID accepts only a text write, and only into a plain text entry (`Edit`, `entry`, or a
vocabulary text field such as macOS `TextArea`) bound to its observed window and semantic container path. Clicks and other
actions on such controls are refused, a password field is never written, and the write succeeds only when its read-back shows
the complete value. Such a field never takes the clipboard and keyboard route; a missing native text interface refuses the write.
Coordinate fallback is unused.
Synthetic order confirmation uses an explicitly configured isolated dialog policy; production policy is not relaxed.
Windows/macOS native qualification and measured model roundtrip/token savings remain separate acceptance work.
