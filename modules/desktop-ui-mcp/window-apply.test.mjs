import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { DesktopUiGateway, applyHint } from './gateway.mjs';
import { parseNativeTree } from './projection.mjs';
import { bindWindowState } from './window-state.mjs';

const inputTools = ['click_element', 'type_text'];

// A main window with one named text field and one named button; the field shows the text that type_text wrote last.
function treeText(fieldValue) {
    return '[1] Window "Fixture" (autoId: "", val: "", enabled: True, offscreen: False)'
        + `\n  [2] Edit "Name" (autoId: "name", val: "${fieldValue}", enabled: True, offscreen: False)`
        + '\n  [3] Button "Save" (autoId: "save", val: null, enabled: True, offscreen: False)';
}

function applyFixture(makeTree = treeText) {
    const calls = [];
    const field = { value: 'old value' };
    const windows = [{ ProcessId: 10, Title: 'Fixture', WindowHandle: 100, Kind: 'main',
        ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }];
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        if (tool === 'type_text') field.value = args.text;
        const result = { success: true, windows, capabilities: { nodeId: 'fixture-node', platform: 'linux-x11-atspi' } };
        if (tool === 'get_ui_tree') {
            result.tree = makeTree(field.value);
            result.snapshot_id = 'fresh';
        }
        return { result: { structuredContent: result } };
    } };
    return { gateway: new DesktopUiGateway(client), calls };
}

// The paths come from the same binding the gateway uses, so the tests do not guess the path format.
function pathOf(member, makeTree = treeText) {
    const bindings = bindWindowState(parseNativeTree(makeTree('old value'))).value;
    return [...bindings.keys()].find(path => bindings.get(path).member === member);
}

function inputCalls(calls) {
    return calls.filter(call => inputTools.includes(call.tool));
}

test('ui_apply refuses malformed steps before any input', async () => {
    const { gateway, calls } = applyFixture();
    await gateway.call('ui_info', {});
    const reply = await gateway.call('ui_apply', { pid: 10, steps: [{ set: 'Name' }] });
    assert.equal(reply.success, false);
    assert.equal(reply.error, 'invalid_steps');
    assert.match(reply.hint, /^Each step is/);
    assert.equal(inputCalls(calls).length, 0);
});

test('ui_apply names an unknown path and sends no input', async () => {
    const { gateway, calls } = applyFixture();
    await gateway.call('ui_info', {});
    const reply = await gateway.call('ui_apply', { pid: 10, steps: [{ call: 'Missing' }] });
    assert.equal(reply.error, 'unknown_path');
    assert.equal(reply.failed_step, 1);
    assert.equal(inputCalls(calls).length, 0);
});

test('ui_apply sets a field and calls a button, reporting the field change of the set step', async () => {
    const { gateway } = applyFixture();
    await gateway.call('ui_info', {});
    const state = await gateway.call('ui_observe', { pid: 10, as: 'state' });
    assert.equal(state.next, applyHint);
    const fieldPath = pathOf('text');
    const buttonPath = pathOf('method');
    const reply = await gateway.call('ui_apply', { pid: 10, shape: state.shape,
        steps: [{ set: fieldPath, value: 'new value' }, { call: buttonPath }] });
    assert.equal(reply.success, true);
    assert.equal(reply.verified, true);
    assert.equal(reply.steps.length, 2);
    assert.deepEqual(reply.steps[0].changed.values[fieldPath], ['old value', 'new value']);
});

test('ui_apply waits for a value that a set step wrote, and reports the wait', async () => {
    const { gateway } = applyFixture();
    await gateway.call('ui_info', {});
    const state = await gateway.call('ui_observe', { pid: 10, as: 'state' });
    const fieldPath = pathOf('text');
    const reply = await gateway.call('ui_apply', { pid: 10, shape: state.shape,
        steps: [{ set: fieldPath, value: 'new value' }, { wait: fieldPath, until: 'value', value: 'new value' }] });
    assert.equal(reply.success, true);
    assert.equal(reply.steps[1].wait, fieldPath);
    assert.equal(reply.steps[1].until, 'value');
});

test('ui_apply stops a wait that never holds at its own step', async () => {
    const { gateway, calls } = applyFixture();
    await gateway.call('ui_info', {});
    const steps = [{ wait: pathOf('text'), until: 'value', value: 'never', timeout_ms: 300 }];
    const reply = await gateway.call('ui_apply', { pid: 10, steps });
    assert.equal(reply.success, false);
    assert.equal(reply.error, 'wait_not_met');
    assert.equal(reply.failed_step, 1);
    assert.equal(reply.failed_step_may_have_changed_ui, false);
    assert.equal(inputCalls(calls).length, 0);
});

test('ui_apply waits for an unbound path to be absent and for a button to be enabled', async () => {
    const { gateway } = applyFixture();
    await gateway.call('ui_info', {});
    const steps = [{ wait: 'Missing', until: 'absent' }, { wait: pathOf('method'), until: 'enabled' }];
    const reply = await gateway.call('ui_apply', { pid: 10, steps });
    assert.equal(reply.success, true);
    assert.equal(reply.steps[0].wait, 'Missing');
    assert.equal(reply.steps[1].until, 'enabled');
});

test('ui_apply refuses a value wait that names no value', async () => {
    const { gateway, calls } = applyFixture();
    await gateway.call('ui_info', {});
    const reply = await gateway.call('ui_apply', { pid: 10, steps: [{ wait: pathOf('text'), until: 'value' }] });
    assert.equal(reply.error, 'invalid_steps');
    assert.equal(inputCalls(calls).length, 0);
});

// The same window with an unnamed field, which is named by its position only.
function unnamedTreeText(fieldValue) {
    return '[1] Window "Fixture" (autoId: "", val: "", enabled: True, offscreen: False)'
        + `\n  [2] Edit "" (autoId: "", val: "${fieldValue}", enabled: True, offscreen: False)`;
}

// The node refuses unknown postcondition arguments, so the keys sent are checked against its published schema.
function nodePostconditionSchema() {
    const contract = JSON.parse(readFileSync(new URL('../../contracts/tools.json', import.meta.url), 'utf8'));
    const tool = contract.tools.find(candidate => candidate.name === 'click_element');

    return tool.inputSchema.properties.postcondition;
}

test('ui_apply hands a wait on a named member to the node as the postcondition of the step before it', async () => {
    const { gateway, calls } = applyFixture();
    await gateway.call('ui_info', {});
    const state = await gateway.call('ui_observe', { pid: 10, as: 'state' });
    const fieldPath = pathOf('text');
    const reply = await gateway.call('ui_apply', { pid: 10, shape: state.shape,
        steps: [{ set: fieldPath, value: 'new value' }, { wait: fieldPath, until: 'value', value: 'new value', timeout_ms: 700 }] });
    assert.equal(reply.success, true);
    assert.equal(reply.steps[1].postcondition, 'node');
    assert.deepEqual(inputCalls(calls)[0].args.postcondition, { name: 'Name', role: 'Edit', auto_id: 'name',
        expected_state: 'exists', expected_text: 'new value', timeout_ms: 700 });
    const declared = Object.keys(nodePostconditionSchema().properties);
    for (const key of Object.keys(inputCalls(calls)[0].args.postcondition)) {
        assert.ok(declared.includes(key), `postcondition key ${key} is declared by the node schema`);
    }
    const click = await gateway.call('ui_apply', { pid: 10, shape: state.shape,
        steps: [{ call: pathOf('method') }, { wait: fieldPath, until: 'enabled' }] });
    assert.equal(click.steps[1].postcondition, 'node');
    assert.equal(inputCalls(calls)[1].args.postcondition.expected_state, 'enabled');
});

test('ui_apply keeps polling for a wait on a positional member and sends no postcondition', async () => {
    const { gateway, calls } = applyFixture(unnamedTreeText);
    await gateway.call('ui_info', {});
    const state = await gateway.call('ui_observe', { pid: 10, as: 'state' });
    const fieldPath = pathOf('text', unnamedTreeText);
    const reply = await gateway.call('ui_apply', { pid: 10, shape: state.shape,
        steps: [{ set: fieldPath, value: 'new value' }, { wait: fieldPath, until: 'value', value: 'new value' }] });
    assert.equal(reply.success, true);
    assert.equal(reply.steps[1].postcondition, undefined);
    assert.equal(inputCalls(calls)[0].args.postcondition, undefined);
});
