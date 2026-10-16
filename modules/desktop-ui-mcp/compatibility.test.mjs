import test from 'node:test';
import assert from 'node:assert/strict';
import { assessCompatibility, captureCompatibility, schemaHashes, assessNativeApiScope } from './compatibility.mjs';

function scenario() {
    const entry = { name: 'editor.write', steps: [{ action: 'write', parameter: 'text', route: 'native-text',
        selector: { role: 'Edit', name: 'Text', autoId: 'text', window: 'main' } }],
        outputs: [{ name: 'text', field: 'value', check: { kind: 'equals_parameter', parameter: 'text' } }],
        inputSchema: { type: 'object', properties: { text: { type: 'string' } } } };
    const actions = ['get_machine_capabilities', 'list_windows', 'get_ui_tree', 'type_text'];
    const current = { node_version: '1.0.1', wire_version: '2025-06-18', platform: 'linux-x11-atspi',
        application: { key: 'editor', version: '0.6.1' }, native_implementation: 'a'.repeat(64),
        gateway_implementation: 'b'.repeat(64), action_schemas: schemaHashes(actions.map(name => ({ name,
            inputSchema: { type: 'object', properties: {} } }))) };
    entry.compatibility = captureCompatibility(entry, current);
    return { entry, current };
}

test('same application/action contracts on a node patch preserve the exact recorded definition without recapture', () => {
    const { entry, current } = scenario();
    const before = JSON.stringify(entry);
    const result = assessCompatibility(entry, { ...current, node_version: '1.0.2' });
    assert.equal(result.status, 'reusable');
    assert.equal(result.reason, 'verified_contract_unchanged');
    assert.equal(JSON.stringify(entry), before);
});

test('changed write implementation requires revalidation and absent action or changed application rejects input', () => {
    const { entry, current } = scenario();
    assert.equal(assessCompatibility(entry, { ...current, native_implementation: 'c'.repeat(64) }).status, 'needs_revalidation');
    const absent = structuredClone(current);
    delete absent.action_schemas.type_text;
    assert.deepEqual(assessCompatibility(entry, absent), { status: 'incompatible', reason: 'required_action_absent', action: 'type_text' });
    for (const application of [{ key: 'other-variant', version: '0.6.1' }, { key: 'editor', version: '0.6.2' }]) {
        assert.equal(assessCompatibility(entry, { ...current, application }).status, 'incompatible');
    }
    assert.equal(assessCompatibility(entry, { ...current, application: { key: 'editor', version: 'unknown' } }).reason, 'identity_unknown');
    assert.equal(assessCompatibility(entry, { ...current, node_version: 'unknown' }).reason, 'identity_unknown');
});

test('only required schemas count; canonical hashes ignore JSON key order and schema changes need new evidence', () => {
    const { entry, current } = scenario();
    current.action_schemas.unrelated_mail = 'c'.repeat(64);
    assert.equal(assessCompatibility(entry, current).status, 'reusable');
    current.action_schemas.type_text = 'c'.repeat(64);
    assert.equal(assessCompatibility(entry, current).reason, 'required_action_schema_changed');
    assert.deepEqual(schemaHashes([{ name: 'read', inputSchema: { type: 'object', properties: {} } }]),
        schemaHashes([{ name: 'read', inputSchema: { properties: {}, type: 'object' } }]));
    const described = [{ name: 'read', inputSchema: { type: 'object', description: 'Input.',
        properties: { path: { type: 'string', description: 'File.' }, description: { type: 'string' } } },
    outputSchema: { type: 'object', description: 'Data.' } }];
    const reworded = [{ name: 'read', inputSchema: { type: 'object', description: 'Arguments.',
        properties: { path: { type: 'string', description: 'Path of the file.' }, description: { type: 'string' } } },
    outputSchema: { type: 'object', description: 'Content.' } }];
    assert.deepEqual(schemaHashes(described), schemaHashes(reworded));
    const renamedArgument = [{ name: 'read', inputSchema: { type: 'object', description: 'Input.',
        properties: { path: { type: 'string', description: 'File.' }, label: { type: 'string' } } },
    outputSchema: { type: 'object', description: 'Data.' } }];
    assert.notDeepEqual(schemaHashes(described), schemaHashes(renamedArgument));
});

test('v1 remains exact-version only, and edited selectors never inherit an earlier approval', () => {
    const { entry, current } = scenario();
    entry.steps[0].selector.name = 'Replacement';
    assert.equal(assessCompatibility(entry, current).reason, 'function_scope_changed');
    entry.compatibility = { node_version: '1.0.1', platform: current.platform, application_version: '0.6.1' };
    assert.equal(assessCompatibility(entry, current).status, 'reusable');
    assert.equal(assessCompatibility(entry, { ...current, node_version: '1.0.2' }).reason, 'legacy_contract_evidence_missing');
    assert.throws(() => captureCompatibility(entry, { ...current, native_implementation: null }), /actual wire/);
});

test('native API reuse requires an explicit tested range and fresh nonmutating probes for this function; COM alone is insufficient', () => {
    const contract = { application_key: 'native-editor', platform: 'windows', function: 'documents.read',
        tested_range: { minimum: '16.0.100', maximum: '16.0.200', evidence_ref: 'reviewed-range' }, required_capabilities: ['documents.read'] };
    const current = { application: { key: 'native-editor', version: '16.0.150' }, platform: 'windows',
        probes: { 'documents.read': { source: 'native_api_probe', function: 'documents.read', mutating: false,
            available: true, evidence_ref: 'actual-read-probe' } } };
    assert.equal(assessNativeApiScope(contract, current).status, 'reusable');
    assert.equal(assessNativeApiScope({ ...contract, tested_range: null }, current).reason, 'tested_native_api_range_missing');
    assert.equal(assessNativeApiScope(contract, { ...current, application: { key: 'native-editor', version: '16.0.201' } }).status, 'incompatible');
    assert.equal(assessNativeApiScope(contract, { ...current, probes: { COM: true, VBA: true } }).status, 'needs_revalidation');
    for (const probe of [{ ...current.probes['documents.read'], mutating: true },
        { ...current.probes['documents.read'], function: 'unrelated.read' }]) {
        assert.equal(assessNativeApiScope(contract, { ...current, probes: { 'documents.read': probe } }).status, 'needs_revalidation');
    }
});
