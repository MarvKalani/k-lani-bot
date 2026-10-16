import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { DesktopUiGateway } from './gateway.mjs';
import { CompatibilityEvidence, runIsolatedAcceptance } from './compatibility-evidence.mjs';
import { contractHash } from './compatibility.mjs';

async function setup(context) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-compatible-gateway-'));
    context.after(() => fs.rmSync(root, { recursive: true, force: true }));
    let version = '1.0.1';
    let native = 'a'.repeat(64);
    let value = '';
    let started = '2026-10-08T00:00:00Z';
    const calls = [];
    const actions = ['get_machine_capabilities', 'list_windows', 'get_ui_tree', 'type_text'];
    const descriptors = actions.map(name => ({ name, inputSchema: { type: 'object', properties: {} } }));
    const client = { protocolVersion: '2025-06-18',
        request: async () => ({ result: { tools: descriptors } }),
        call: async (tool, args) => {
            calls.push(tool);
            const payload = { success: true };
            if (tool === 'get_machine_capabilities') payload.capabilities = { version, platform: 'linux-x11-atspi',
                nativeImplementationSha256: native, nodeId: 'private-node', activeTools: actions };
            if (tool === 'list_windows') payload.windows = [{ ProcessId: 10, WindowHandle: 100, Kind: 'main', Title: 'Editor', ProcessStartedAtUtc: started }];
            if (tool === 'get_ui_tree') {
                payload.snapshot_id = 'fresh';
                payload.tree = '[1] Window "Editor" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                    + '  [2] Edit "Text" (autoId: "text", val: ' + JSON.stringify(value) + ', enabled: True, offscreen: False)';
            }
            if (tool === 'type_text') value = args.text;
            return { result: { structuredContent: payload } };
        } };
    const compatibility = { evidenceRoot: path.join(root, 'private-evidence'),
        authority: { node: 'private-node', principal: 'owner', connection: 'authorized-client' } };
    const configuration = { cartridgeRoot: path.join(root, 'cartridges'), revisionStateRoot: path.join(root, 'private-revisions'),
        application: { key: 'editor-fixture', version: '1.0', identity_source: 'controlled-test-fixture' }, auto_capture: false };
    const gateway = new DesktopUiGateway(client, () => {}, configuration, { compatibility });
    gateway.serverInfo = { name: 'node', version };
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.name === 'Text').ref;
    const demonstration = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'PRIVATE_TRAINING' }] });
    await gateway.call('cartridge_call', { function: 'Programming.learn', arguments: { name: 'editor.write', topic: 'Use/Editor',
        evidence: demonstration.evidence, parameters: ['text'], outputs: [{ name: 'text', ref, field: 'value' }] } });
    const call = (name, args) => gateway.call('cartridge_call', { function: name, arguments: args });
    return { root, gateway, call, compatibility, calls, descriptors,
        patch: () => { version = '1.0.2'; }, changeWriter: () => { native = 'b'.repeat(64); },
        replaceWindow: () => { started = '2026-10-08T01:00:00Z'; }, actual: () => value };
}

test('gateway reuses a verified modern function on a node patch without changing its file or bypassing fresh target checks', async context => {
    const fixture = await setup(context);
    const before = fs.readFileSync(fixture.gateway.learned.file, 'utf8');
    fixture.patch();
    const info = await fixture.call('Info.compatibility', { function: 'editor.write' });
    assert.equal(info.status, 'reusable');
    assert.ok(info.evidence_ref);
    const result = await fixture.call('editor.write', { pid: 10, parameters: { text: 'new input' } });
    assert.equal(result.verified, true);
    assert.equal(fixture.actual(), 'new input');
    assert.equal(fs.readFileSync(fixture.gateway.learned.file, 'utf8'), before);
    fixture.replaceWindow();
    const count = fixture.calls.filter(tool => tool === 'type_text').length;
    const replaced = await fixture.call('editor.write', { pid: 10, parameters: { text: 'never' } });
    assert.equal(replaced.input_sent, false);
    assert.match(replaced.error, /replaced/);
    assert.equal(fixture.calls.filter(tool => tool === 'type_text').length, count);
});

test('changed writer blocks routine input; caller flags, read-probes and ordinary Info evidence cannot revalidate it', async context => {
    const fixture = await setup(context);
    fixture.changeWriter();
    const info = await fixture.call('Info.compatibility', { function: 'editor.write' });
    assert.equal(info.status, 'needs_revalidation');
    assert.equal(info.reason, 'implementation_changed');
    const before = fixture.calls.filter(tool => tool === 'type_text').length;
    await assert.rejects(fixture.call('editor.write', { pid: 10, parameters: { text: 'not sent' } }), /compatibility changed/);
    await assert.rejects(fixture.call('Programming.revalidate', { function: 'editor.write', expected_revision: info.revision,
        evidence_ref: info.evidence_ref, passed: true }), /Invalid compatibility arguments/);
    await assert.rejects(fixture.call('Programming.revalidate', { function: 'editor.write', expected_revision: info.revision,
        evidence_ref: info.evidence_ref }), /unknown_acceptance_evidence/);
    const evidence = fixture.gateway.compatibilityRuntime.evidence;
    assert.throws(() => evidence.record(fixture.gateway.learned.get('editor.write'), fixture.gateway.compatibilityRuntime.current(),
        { test: 'read_only', isolation: { kind: 'private_virtual_desktop', node: 'private-node', purpose: 'synthetic_acceptance' },
            verified: true, independent_oracle_digest: 'c'.repeat(64), execution_digest: 'd'.repeat(64) }), /functional acceptance/);
    assert.equal(fixture.calls.filter(tool => tool === 'type_text').length, before);
});

test('independently checked isolated effect authorizes one atomic CAS revision, with no recapture or training values', async context => {
    const fixture = await setup(context);
    fixture.changeWriter();
    const old = fs.readFileSync(fixture.gateway.learned.file, 'utf8');
    const isolation = { kind: 'private_virtual_desktop', node: 'private-node', purpose: 'synthetic_acceptance' };
    const proof = await runIsolatedAcceptance(fixture.gateway, fixture.gateway.compatibilityRuntime.evidence,
        'editor.write', { text: 'ISOLATED_NEW_EFFECT' }, isolation, async () => ({
            verified: fixture.actual() === 'ISOLATED_NEW_EFFECT', digest: contractHash(fixture.actual()) }));
    const revision = fixture.gateway.learned.generation;
    const accepted = await fixture.call('Programming.revalidate', { function: 'editor.write', expected_revision: revision,
        evidence_ref: proof.evidence_ref });
    assert.equal(accepted.revision, revision + 1);
    assert.equal(accepted.status, 'reusable');
    assert.equal((await fixture.call('Info.compatibility', { function: 'editor.write' })).acceptance_ref, proof.evidence_ref);
    const current = fs.readFileSync(fixture.gateway.learned.file, 'utf8');
    assert.notEqual(current, old);
    assert.equal(current.includes('ISOLATED_NEW_EFFECT'), false);
    assert.equal(JSON.parse(current).functions.length, 1);
    await assert.rejects(fixture.call('Programming.revalidate', { function: 'editor.write', expected_revision: revision,
        evidence_ref: proof.evidence_ref }), /stale_cartridge_revision/);
    const routine = await fixture.call('editor.write', { pid: 10, parameters: { text: 'routine value' } });
    assert.equal(routine.verified, true);
});

test('foreign or tampered receipts and failed independent oracle results cannot approve a writer', async context => {
    const fixture = await setup(context);
    fixture.changeWriter();
    const isolation = { kind: 'private_virtual_desktop', node: 'private-node', purpose: 'synthetic_acceptance' };
    await assert.rejects(runIsolatedAcceptance(fixture.gateway, fixture.gateway.compatibilityRuntime.evidence,
        'editor.write', { text: 'test' }, isolation, async () => ({ verified: false, digest: 'c'.repeat(64) })), /Independent oracle/);
    const proof = await runIsolatedAcceptance(fixture.gateway, fixture.gateway.compatibilityRuntime.evidence,
        'editor.write', { text: 'test' }, isolation, async () => ({ verified: fixture.actual() === 'test', digest: contractHash(fixture.actual()) }));
    const foreign = new CompatibilityEvidence({ ...fixture.compatibility, authority: { ...fixture.compatibility.authority, connection: 'foreign' } });
    const entry = fixture.gateway.learned.get('editor.write');
    assert.throws(() => foreign.read(proof.evidence_ref, entry, fixture.gateway.compatibilityRuntime.current()), /foreign_or_stale/);
    const file = path.join(fixture.compatibility.evidenceRoot, proof.evidence_ref + '.json');
    const record = JSON.parse(fs.readFileSync(file, 'utf8'));
    record.payload.proof.test = 'read_only';
    fs.writeFileSync(file, JSON.stringify(record));
    await assert.rejects(fixture.call('Programming.revalidate', { function: 'editor.write', expected_revision: fixture.gateway.learned.generation,
        evidence_ref: proof.evidence_ref }), /forged_acceptance_evidence/);
});

test('v1 load is explicit and nondestructive; unknown future format remains byte-for-byte unchanged', async context => {
    const fixture = await setup(context);
    const document = JSON.parse(fs.readFileSync(fixture.gateway.learned.file, 'utf8'));
    document.version = 1;
    document.functions[0].compatibility = { node_version: '1.0.1', platform: 'linux-x11-atspi', application_version: '1.0' };
    const old = JSON.stringify(document);
    fs.writeFileSync(fixture.gateway.learned.file, old);
    fixture.gateway.learned.functions = fixture.gateway.learned.load();
    assert.equal(fixture.gateway.learned.sourceVersion, 1);
    fixture.patch();
    assert.equal((await fixture.call('Info.compatibility', { function: 'editor.write' })).reason, 'legacy_contract_evidence_missing');
    assert.equal(fs.readFileSync(fixture.gateway.learned.file, 'utf8'), old);
    document.version = 99;
    const unknown = JSON.stringify(document);
    fs.writeFileSync(fixture.gateway.learned.file, unknown);
    assert.throws(() => fixture.gateway.learned.load(), /format does not match/);
    assert.equal(fs.readFileSync(fixture.gateway.learned.file, 'utf8'), unknown);
});
