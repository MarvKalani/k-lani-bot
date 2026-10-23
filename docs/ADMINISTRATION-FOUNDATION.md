# Administration data, efficiency, backups and credentials

k-lani-bot's administration scope includes software inventory, installation and removal, configuration changes,
backup and recovery. Business applications are examples of this scope. The controlling AI identifies the actual
computer, application/version, permissions and expected effect, qualifies the needed action and reuses it through MCP.
Inventory coverage must stay explicit: an available desktop application list does not prove that every package,
service or portable executable on that computer was discovered.

This page describes the first implementation components and their current boundaries. It does not grant permission
to install/uninstall software, change a live node or migrate existing databases.

## What is implemented

| Component | Available behavior | Remaining integration |
| --- | --- | --- |
| Local efficiency summary | WPF step log and copied log show retained outcomes, measured argument/result JSON bytes and execution time | Complete job/phase aggregation, client-reported model tokens and matched baseline comparison |
| Closed-file backup | Bounded plans, encrypted archives, freshness checks and verified restoration into a new directory; core node MCP tools `backup_plan`, `backup_verify`, `backup_create`, `backup_restore`; backup requirement before changes, switchable per node or fleet from the master | Consistent application exports, production restore orchestration, sandbox nodes for the remote switch |
| Credential broker | Exact node/principal/application/purpose grants, expiry/revocation checks, short-lived zeroed buffers | Persistent operator-managed grants, provisioning UI and administrative/MCP routing |
| Controller tray | Master lists registered nodes and the MCP clients using them; pause/resume per client and bot on/off, refused calls answer `access_paused`; Linux tray (AppIndicator), Windows tray cross-built | Native Windows acceptance, macOS tray; see [controller tray](CONTROLLER-TRAY.md) |
| Linux Secret Service provider | Native binary storage/read/delete in a scoped OS keyring with bounded cancellation | Other OS providers and locked-session/unattended deployment qualification |

The backup tools are callable on the core node MCP route and, as `cartridge_call` functions of the same names, through
the native compact and cartridge gateways (not yet the optional Node.js compact frontend); the credential broker remains a
trusted node-side API.
No existing node permissions, pairing state, active application data or credential stores are migrated automatically.

## Efficiency: show measurements, not guesses

`StepEfficiency` aggregates the retained `StepList`, excluding planned steps and using the latest state of each
operation ID. Completed, failed and uncertain entries are distinct. Failed work remains part of measured effort.
The view's measurement coverage is explicit, and the retained history is not presented as an entire session's totals.

`StepDiagnostics.ArgumentJsonBytes` and `ResultJsonBytes` measure node action JSON, not network billing or tokens
delivered to a model. Missing/invalid counters remain unknown; overflow cannot wrap into a deceptively small total.
The local node cannot infer model usage, so the display says that model tokens were not reported. No token-saving
percentage is invented. Rendering and copying the summary cause no model request or application observation.

The summary is integrated into the existing WPF step window and text export. Cross-platform builds and presenter tests
do not constitute native Windows rendering acceptance; that remains a separate private-session check.

## Closed-file backup core

`FileBackupService` receives three trusted, dedicated, non-overlapping roots: source files, encrypted archives and
private recovery output. They must have suitable owner-only access; on Windows this includes appropriate inherited
ACLs. The current native file/vault combination is qualified on Linux, not on every target operating system.

The C# API offers `TryPlan`, `TryCreate`, `TryVerify`, `TryCheckCurrent` and `TryRestore`.

### Node tools and operator setup

The operator configures backup per node; an agent can choose neither folders nor key:

1. Create the three folders. Archives and restores must lie outside the agent workspace, the node state and each
   other, and must not contain the policy file. The source may lie inside the workspace.
2. Run `k-lani-bot-node --create-backup-key --policy policy.json` on the node. It stores 32 random bytes in the
   node's Linux Secret Service scope and prints only `keyId` and `keyVersion`. Other systems refuse; there is no file fallback.
3. Add a `backup` section to the policy the actuator reads (`worker-policy.json` for nodes created by `--init`) with
   `source`, `archives`, `restores`, `keyId`, `keyVersion` and optionally `maximumAgeHours` (default 24, at most 720),
   then restart the node. Root or key changes need a restart.
4. Grant the scopes by editing the policy: `backup.read` (plan, verify), `backup.create`, `backup.restore`.
   `--init` grants none of them.

| Tool | Scope | Effect |
| --- | --- | --- |
| `backup_plan` | `backup.read` | Counts the source files and returns `backup_id` and `fingerprint` |
| `backup_create` | `backup.create` | Plans again on the node; archives only if the fingerprint still matches; never replaces an archive |
| `backup_verify` | `backup.read` | Decrypts and checks an archive; `current` says whether it is within the maximum age and the source unchanged |
| `backup_restore` | `backup.restore` | Restores into a new folder name below the recovery root; never overwrites |

Answers carry identifiers, counts, times and fingerprints, never file names, content or the recovery folder's path.
The vault key is granted per call to the authenticated principal, for backup encryption only, until the principal expires.
A key rotation that still needs old archives' keys is not yet supported.

1. Plan a quiescent ordinary file set, with bounded relative filenames, lengths and SHA-256 digests.
2. Obtain a 32-byte encryption key through a credential grant specifically for `BackupEncryption`.
3. Read the planned bytes, restore them into an isolated new directory and compare the restored fingerprint.
4. Recheck the source fingerprint and publish an AES-256-GCM archive binding its identity and opaque key reference.
5. Decrypt and verify the written archive before returning a receipt. Capture time and verification time remain distinct.

On Unix, publication also synchronizes directory metadata and its ancestors; restore synchronizes its new tree before
publishing the destination. An unsupported or failed directory flush prevents a valid result. Windows native power-loss
and ACL qualification remains separate from the cross-build.

A current-source check rejects changed or expired evidence. Rechecking an old archive does not make its capture newer.
Restore writes into a private staging directory, checks its bytes and publishes only a new destination name. Existing
directories are never overwritten. No key is copied into the archive and there is no plaintext fallback.

Current bounds are 256 files, 8 MiB per file and 16 MiB of source content. Symbolic links, overlapping roots, invalid relative
paths and known database file types are rejected. This first adapter preserves file content, not complete OS metadata,
empty-directory structure, services, registry state or application transactions. Sources must be quiescent regular files.
Open databases require a dedicated consistent export/snapshot adapter; whole-machine recovery and in-place production
restore are not provided by this component.

### Backup requirement before changes

`backupRequirement` in the actuator's policy is `required` unless the administrator writes `off`; `--init` takes
`--backup-requirement required|off` (default `required`). With `required`, the first change of each session is refused
with `backup_required` and `not_dispatched` unless the archive last written by `backup_create` on this node still matches
the source and is within the maximum age. Later changes of that session stay admitted until the archive expires, because
they change the source as expected. A node without a `backup` section refuses every change until backup is configured or
the requirement is switched off. Creating or restoring a backup, saving a macro and ending a session are exempt; macro
steps are checked one by one. The setting is read again for every change, so editing it needs no restart.
The check proves a current backup of the configured source, not that the data a particular change touches lies inside it.
The administrator switches it remotely on the master's console for one node, a comma-separated list or all paired nodes:
`backup-requirement NODE|NODE,NODE|all required|off`. Connected nodes apply it at once; the master keeps the order for
offline nodes and for nodes whose version does not yet understand it, and sends it when they connect with one that does. `backup-requirement-status` lists each node's order and whether it was
applied. The node writes the change to both policies and journals it as `backup_requirement_changed_by_master_admin`.
Tools an AI principal calls cannot change it. A container sandbox mounts the actuator policy read-only, so there the
order stays pending; that case is not yet covered.

For installation/removal or a configuration change, the eventual administrative action must declare the data/configuration
it can affect. That scope determines the backup adapter and restore checks. A source-code archive is not evidence that
an application's database, registry entries or installed files can be restored.

## Credentials stay outside the action and model context

`SecretReference` is an opaque ID and revision. `CredentialBroker` checks an administrator-supplied grant against the
actual node, principal, application and purpose before reading, and checks again after provider access. Trusted handlers
must supply authenticated identities; these fields must never be taken as authority merely because a model submitted them.

The broker does not cache plaintext between operations. `SecretLease` exposes bytes only inside the trusted node assembly;
serialization reveals its length, and disposal zeroes the owned buffer. Provisioning must allocate a new reference/revision
for a replacement key and retain old backup keys according to the recovery policy. The first broker does not itself
implement a durable provisioning, rotation or grant-management workflow.
Secret-aware handler integration must keep references in ordinary tool arguments and audit records. Resolving a secret
and inserting its value into the existing `type_text` argument would put it on the wrong recording boundary; that is not
a supported integration. Resolve only inside the trusted operation, suppress plaintext from results/errors/traces, and
verify the authorized effect without returning the secret. A timed-out provider write needs reconciliation by its existing
reference, not blind reprovisioning.

`LinuxSecretServiceProvider` uses the installed libsecret binary API (0.19 or later) and the user's Secret Service.
It uses no model API or Python runtime. A dedicated namespace plus reference/revision scopes lookups and deletion.
Native values are released and errors return a typed failure rather than potentially secret-bearing error text.
The provider requests cancellation after ten seconds; invoke it off UI threads. A usable, unlocked keyring is a deployment
prerequisite for unattended operation. Other OSes or unavailable dependencies fail without a fallback credential file.
[Native API contract](https://gnome.pages.gitlab.gnome.org/libsecret/func.password_storev_binary_sync.html).

## ACID Fox / k-lani-core assessment

The inspected core revision is `2cf76993af17eb2e9df43e46ef050437ee3497c6`. Its existing WORM, table-group and table-group-crash
test suites passed 13 tests in a disposable build target. This is focused correctness evidence, not whole-engine certification.
No ACID Fox source or existing data was changed.

It is a good candidate for local administrative persistence without requiring PostgreSQL:

- WORM tables for immutable action revisions, qualification decisions, audit events and measured usage history.
- Ordinary tables or indexed projections for current inventory, job state, active bindings and revocation state.
- Explicit group transactions where several related records must commit together.
- Typed indexed fields for node/application/job IDs, kind, state and time. Original documents and archive references
  can be attachments; an opaque JSON body must not be the only queryable representation.

The ACID Fox C# demonstration in `examples/csharp-client-demo/KLaniClient.cs` still uses `TcpClient`; it is not an embedded
production binding. This is separate from k-lani-bot's node transport, which prefers HTTP/3 over QUIC and retains an
authenticated HTTP/2 fallback. An embedded database binding would not need either network transport. A small packaged core
adapter still needs qualification for the node's lifecycle, concurrency, failure isolation, packaging and recovery.
Pin a reviewed engine revision and test the exact C# integration route before migration. Do not open the same writable
database group independently from several gateway processes and assume it supports multiple owners.

The core has a separate optional field-encryption provider, but key management remains an application responsibility.
Keep live credential values and encryption keys in the protected provider; store references and audit metadata in the
database. WORM history is not an erasure or key-rotation mechanism, and WORM alone does not replace a backup.
An ACID Fox backup adapter must qualify its group manifest, WAL, table/blob data and reopen/recovery behavior. The
ordinary closed-file adapter deliberately refuses `.mkx`/WAL files rather than claiming a safe live database backup.

## Verification

```bash
ulimit -c 0
dotnet run --project tests/NodeRegression/NodeRegression.csproj
scripts/check.sh
```

`scripts/check.sh` includes a native Secret Service probe when its OS prerequisites are available. It starts a private
display/bus and empty keyring, exercises binary credentials and encrypted backup/restore, then removes only its own
fixture. The operator's real keyring and desktop are not test inputs. Missing prerequisites are reported as a skip.
Windows UI/native credential acceptance, complete administrative routing and the database adapter remain separate work.
