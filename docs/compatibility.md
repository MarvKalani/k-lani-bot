# Cartridge compatibility and revision recovery

The compact gateway separates node product labels from the wire/action contract actually needed by one function.
Generated compatibility v2 records negotiated MCP wire version, required input/output schema hashes, exact application
variant/build/platform, accessibility or keyboard route, native/host implementation fingerprints and selector/function scope.
A patch label with identical contracts reuses the existing definition. Fresh instance, target/type and output checks still apply.

The node's `nativeImplementationSha256` comes from compiled assembly metadata generated with MSBuild's
[GetFileHash task](https://learn.microsoft.com/en-us/visualstudio/msbuild/getfilehash-task).
It fingerprints node C# source, target/compiler symbols and package references, excluding the declarative product identity file.
The gateway fingerprints its executable module sources. These are conservative implementation change detectors;
they are not binary signatures or claims that every application/backend is qualified.

Browse `Info.compatibility`, then call it with `{function}`. The compact response contains status, machine-readable
reason, revision and `evidence_ref`. Full recorded/observed fingerprints are available privately through `Info.result`.
Function discovery retains the compact legacy version fields rather than sending the full fingerprint matrix every time.

| Status | Meaning |
| --- | --- |
| reusable | Recorded scope and the current required contracts match |
| needs_revalidation | Contracts/implementation changed or required evidence is unknown |
| incompatible | Application/wire/function scope changed or a required action is absent |

Generated UI selectors remain bound to an exact build. The separate native API scope assessor requires an explicitly
reviewed tested version range and current nonmutating probes for that function's named capabilities. COM/VBA availability
alone is insufficient. It does not turn a generated UI recipe into a native API function or loosen its exact-build checks.

## Isolated acceptance

Configure `compatibility` privately on the host gateway with `evidenceRoot` and trusted
`authority: {node, principal, connection}`. Keep the evidence root separate from cartridge exports and source control.
It stores a local mode-600 receipt signing key and signed receipts; these are distinct from node pairing credentials.
`learning.revisionStateRoot` selects the private recovery directory. Its default is `.k-lani-cartridge-revisions`
beside the configured cartridge root, outside the application/version directory.

The reviewed host acceptance runner calls `runIsolatedAcceptance` with an authorized private node, synthetic scope,
selected function/parameters and an independent application oracle. The included runner accepts controlled fixtures
or isolated Mousepad test documents. It is a host API, not a model-facing way to claim a successful test.
Scope is checked before input and after the independent oracle; changed writers require a functional effect test.
Production mailbox revalidation is excluded. No approval is inferred from a screenshot, successful read or caller flag.

`Programming.revalidate` takes `{function, expected_revision, evidence_ref}`. It checks the private receipt signature,
authorized target, prior function/compatibility scope and fresh current contracts. A missing, forged, foreign or stale
receipt is rejected. Accepted metadata replaces the same active cartridge atomically and keeps the receipt reference.
It does not operate the production application, change pairing keys or create another catalog entry for a revision.

For the native EXE profile, use the same routing names and an optional private `--ui-config` file:

```json
{
  "version": 1,
  "compatibility": {
    "evidenceRoot": "/absolute/private/acceptance",
    "authority": {
      "node": "ACTUAL_AUTHORIZED_NODE_ID",
      "principal": "AUTHORIZED_ISSUER_PRINCIPAL",
      "connection": "AUTHORIZED_ISSUER_CONNECTION"
    }
  }
}
```

Replace the placeholders with the reviewed issuer's existing target and authority. The node ID is the actual ID from
core capabilities, not the registry entry name. Keep this file owner-only and outside cartridge exports; native startup
validates its absolute paths, bounds and version before opening clients. Unsupported fields/versions and linked files
are refused without creating a receipt store or repairing permissions. Omit `--ui-config` for ordinary compact operation.
`Info.compatibility` also works without private acceptance configuration or learning. Native revalidation requires an
existing key/receipt and a learning root for atomic metadata replacement. The gateway never issues acceptance receipts.

Native host code can issue reviewed evidence through `CompactAcceptanceStore` after an isolated functional test and
independent oracle. The native reader consumes that store's signed protocol. The JavaScript `runIsolatedAcceptance`
runner qualifies its own frontend; its gateway fingerprint is not interchangeable with the native frontend's fingerprint.
A synthetic issuer unit test proves the signature/revision boundary, not installed-application acceptance. Both issuer
and reader must observe matching native gateway/function/selector/application contracts for a real acceptance run.

## File versions and interruption

Existing v1 generated files are explicitly readable without rewriting them. They remain restricted to the exact old
node version because they contain no historic schema/implementation fingerprints. Validated writes or explicit revalidation
migrate the document to v2 while retaining existing functions and candidate history. Unknown newer formats are rejected
without changing the original bytes; a missing build is never replaced with a fixture version implicitly.

Revision updates compare both numeric revision and original file digest under an exclusive lock. The temporary file
is flushed before replacement, and a private recovery journal records old/new digests. A crash before replacement retains
the old valid revision; a crash after replacement recognizes the new valid revision. Corrupt temporary state or external
edits produce an explicit recoverable error. They are not silently overwritten. Linux lock recovery requires proof that
the recorded process lifetime ended; uncertain/live locks are refused. Native qualification on other hosts remains separate.

New workflow checkpoints hash recipe bodies separately from accepted compatibility metadata, so a matching metadata-only
revision can reconcile a pending effect without replay. Recipe changes still invalidate the checkpoint. Older checkpoints
retain their original full-entry hash; without the missing body evidence they conservatively remain unconfirmed after changes.

## Synthetic compatibility lab

`CompatibilityFixtureClient` and the private-config `compatibility-core.mjs` proxy provide explicitly labelled synthetic
metadata builds A/B and an incompatible writer contract. They preserve actual native implementation fingerprints and
forward application observations/results unchanged. The negative fixture removes `type_text` and refuses native input
before forwarding. These fixtures are not production releases, model providers or authentic Computer Use.
Use the same shared proxy for old and current frontend comparisons so the baseline does not need new fixture support.
Never apply both the shared proxy and the server's `compatibilityFixture` option to one connection.

Native acceptance uses the real GTK fixture in an isolated desktop and an independent AT-SPI read. Model comparisons
still need the exact accepted worker image, reviewed host adapter and explicit private budget. Missing usage remains unknown.
