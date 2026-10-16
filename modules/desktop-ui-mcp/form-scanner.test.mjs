import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { FormScanner } from './form-scanner.mjs';
import { LearnedFunctions } from './learned-functions.mjs';
import { parseNativeTree } from './projection.mjs';
import { DesktopUiGateway } from './gateway.mjs';

function setup(context) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-form-scanner-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const learning = new LearnedFunctions({ cartridgeRoot: directory,
        application: { key: 'form-fixture', version: '1.0', identity_source: 'controlled-test-fixture' } });
    return { directory, learning, scanner: new FormScanner(learning) };
}

function tree(value = 'PRIVATE_VALUE', extra = '') {
    return parseNativeTree('[1] Window "Form" (autoId: "", val: "", enabled: True, offscreen: False)\n'
        + '  [2] Edit "Name" (autoId: "name", val: ' + JSON.stringify(value) + ', enabled: True, offscreen: False)\n'
        + '  [3] button "Submit" (autoId: "submit", val: null, enabled: True, offscreen: False)' + extra);
}

const args = { pid: 10, form: 'address', window: 1 };
const observed = { WindowHandle: 1, Kind: 'main' };
const compatibility = { node_version: 'fixture', platform: 'fixture', application_version: '1.0' };

test('valid names matching object properties do not inherit a phantom earlier observation', context => {
    const { learning, scanner } = setup(context);
    assert.throws(() => scanner.read({ form: 'constructor' }), /Unknown observed form/);
    scanner.scan(tree(), { ...args, form: 'constructor' }, compatibility, observed);
    const read = new FormScanner(learning).read({ form: 'constructor' });
    assert.equal(read.observed_states, 1);
    assert.deepEqual(read.observed_transitions, []);
});

test('scanner persists discovery material without input values, IDs, PIDs or executable claims', context => {
    const { learning, scanner } = setup(context);
    const result = scanner.scan(tree(), { ...args, contents: true }, compatibility, observed);
    assert.equal(result.fields[0].type, 'text');
    assert.equal(result.fields[1].type, 'action');
    assert.equal(result.contents.fields[0].value, 'PRIVATE_VALUE');
    assert.ok(result.unknown.includes('required'));
    assert.match(result.action_status, /not verified or executable/);
    const file = fs.readFileSync(scanner.file, 'utf8');
    assert.equal(file.includes('PRIVATE_VALUE'), false);
    assert.equal(file.includes('"pid"'), false);
    assert.equal(file.includes('"id"'), false);
    assert.equal(new FormScanner(learning).read({ form: 'address' }).fields.length, 2);
    assert.equal(learning.functions.length, 0);
});

test('content changes preserve state; later appearing controls add observed states without inventing their cause', context => {
    const { scanner } = setup(context);
    const first = scanner.scan(tree(), args, compatibility, observed);
    const valueChanged = scanner.scan(tree('different'), args, compatibility, observed);
    assert.equal(valueChanged.state, first.state);
    assert.equal(valueChanged.observed_states, 1);
    const next = scanner.scan(tree('different',
        '\n  [4] Edit "Company" (autoId: "company", val: "", enabled: True, offscreen: False)'), args, compatibility, observed);
    assert.equal(next.observed_states, 2);
    assert.equal(next.change.added.length, 1);
    const record = scanner.read({ form: 'address' });
    assert.match(record.observed_transitions[0].cause, /unknown/);
    assert.equal(scanner.read({ form: 'address', state: first.state }).fields.length, 2);
});

test('ambiguous or disabled targets remain non-ready material; missing scopes and oversized forms are refused', context => {
    const { scanner } = setup(context);
    const duplicate = tree('', '\n  [4] Edit "Other name" (autoId: "name", val: "", enabled: True, offscreen: False)');
    const result = scanner.scan(duplicate, args, compatibility, observed);
    assert.equal(result.fields[0].target_ready, false);
    assert.equal(result.fields[2].target_ready, false);
    const disabled = tree();
    disabled[1].enabled = false;
    assert.equal(scanner.scan(disabled, args, compatibility, observed).fields[0].target_ready, false);
    assert.throws(() => scanner.scan(tree(), { ...args, window: 0 }, compatibility, observed), /observed PID/);
    const many = tree();
    many.push(...Array.from({ length: 201 }, (_, index) => ({ ...many[1], id: index + 10 })));
    assert.throws(() => scanner.scan(many, args, compatibility, observed), /narrow/);
    assert.throws(() => scanner.scan(tree(), { ...args, max_chars: 0 }, compatibility, observed), /bounded/);
});

test('live content bounds are explicit and offscreen changes are observations, not inferred conditions', context => {
    const { scanner } = setup(context);
    const first = scanner.scan(tree(), { ...args, contents: true, max_chars: 4 }, compatibility, observed);
    assert.equal(first.contents.fields[0].value, 'PRIV');
    assert.equal(first.contents.fields[0].value_truncated, true);
    const nodes = tree();
    nodes[1].offscreen = true;
    const next = scanner.scan(nodes, args, compatibility, observed);
    assert.notEqual(first.state, next.state);
    assert.equal(next.change.changed.length, 1);
    assert.match(next.offscreen_meaning, /does not prove/);
});

test('toggle buttons are choices rather than CTA assertions, and multiple outer windows require an explicit scope', context => {
    const { scanner } = setup(context);
    const nodes = tree('', '\n  [4] toggle button "Details" (autoId: "details", val: null, enabled: True, offscreen: False)');
    const result = scanner.scan(nodes, { pid: 10, form: 'address' }, compatibility, observed);
    assert.equal(result.fields[2].type, 'toggle');
    assert.deepEqual(result.fields[2].actions, ['read', 'toggle']);
    assert.equal(scanner.scan(tree(), { ...args, contents: true }, compatibility, observed).contents.fields[0].key, result.fields[0].key);
    nodes.push({ ...nodes[0], id: 20, name: 'Other' });
    assert.throws(() => scanner.scan(nodes, { pid: 10, form: 'address' }, compatibility, observed), /ambiguous/);
    assert.throws(() => scanner.scan(nodes, args, compatibility, observed), /ambiguous/, 'a tree of several windows names no window');
});

test('compact form scans retain counts, changes and full private details without persisting field values', async context => {
    const { directory, learning } = setup(context);
    const text = 'ä😀'.repeat(6000);
    const nodes = tree(text);
    const nativeTree = nodes.map(node => ' '.repeat(node.depth) + '[' + node.id + '] ' + node.role + ' ' + JSON.stringify(node.name)
        + ' (autoId: ' + JSON.stringify(node.autoId) + ', val: ' + JSON.stringify(node.value) + ', enabled: True, offscreen: False)').join('\n');
    const calls = [];
    const client = { call: async tool => {
        calls.push(tool);
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, WindowHandle: 1, Kind: 'main', ProcessStartedAtUtc: "2026-10-08T00:00:00Z", Title: 'Form' }];
        if (tool === 'get_ui_tree') result.tree = nativeTree;
        return { result: { structuredContent: result } };
    } };
    const gateway = new DesktopUiGateway(client, () => {}, { cartridgeRoot: directory, application: learning.application });
    gateway.serverInfo = { version: 'fixture' };
    await gateway.call('ui_info', {});
    const summary = await gateway.call('cartridge_call', { function: 'Info.scan_form', arguments: { ...args, result_mode: 'summary' } });
    assert.equal(summary.field_count, 2);
    assert.equal(summary.fields, undefined);
    assert.equal(summary.change.added.length, 2);
    const page = await gateway.call('cartridge_call', { function: 'Info.result',
        arguments: { evidence_ref: summary.evidence_ref, section: 'debug' } });
    assert.equal(JSON.parse(page.text).fields.length, 2);
    const values = await gateway.call('cartridge_call', { function: 'Info.scan_form', arguments: { ...args, result_mode: 'values' } });
    const field = Object.values(values.outputs).find(output => output.truncated);
    assert.equal(Array.from(field.text).length, 2000);
    assert.equal(field.verified, undefined);
    assert.equal(field.utf8_length, Buffer.byteLength(text));
    const before = calls.length;
    await assert.rejects(gateway.call('cartridge_call', { function: 'Info.scan_form',
        arguments: { ...args, result_mode: 'values', fields: [] } }), /Invalid result fields/);
    assert.equal(calls.length, before);
    assert.equal(fs.readFileSync(gateway.forms.file, 'utf8').includes(text), false);
});
