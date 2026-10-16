import test from 'node:test';
import assert from 'node:assert/strict';
import { DesktopUiGateway, unnamedWriteAllowed, unnamedTargetRefusal, unnamedTextFieldRefusal } from './gateway.mjs';

function fixture(setter, ambiguous = false, extraRow = '') {
    const calls = [];
    let value = '';
    let clipboard = '';
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        let result = { success: true };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: 'Fixture', WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: "2026-10-08T00:00:00Z" }];
        if (tool === 'get_ui_tree') {
            result.tree = '[1] Window "Fixture" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [99] Edit "Field" (autoId: "", val: ' + JSON.stringify(value) + ', enabled: True, offscreen: False)';
            result.snapshot_id = 'fresh';
            result.tree += extraRow;
            if (ambiguous) result.tree += '\n  [100] Edit "Field" (autoId: "", val: "", enabled: True, offscreen: False)';
        }
        if (tool === 'find_elements') result = { success: true, total_matches: ambiguous ? 2 : 1,
            matches: [{ Id: 99, IsEnabled: true }], snapshot_id: 'fresh' };
        if (tool === 'type_text') {
            result = setter;
            if (setter.success === true) value = args.text;
        }
        if (tool === 'set_clipboard') clipboard = args.text;
        if (tool === 'hotkey' && args.keys.includes('v')) value = clipboard;
        return { result: { structuredContent: result } };
    } };
    return { gateway: new DesktopUiGateway(client), calls };
}

test('invalid recorded decisions reject before native UI input', async () => {
    const { gateway, calls } = fixture({ success: true });
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const decision = { severity: 'light', occasion: 'Synthetic test', choice: 'Confirm', reason: 'Explicit request', expected_result: 'Verified text' };
    const before = calls.length;
    for (const invalid of [null, {}, { ...decision, severity: 'unknown' }, { ...decision, reason: '' }, { ...decision, bypass: true }]) {
        await assert.rejects(gateway.call('ui_act', { pid: 10, decision: invalid, steps: [{ action: 'write', ref, text: 'text' }] }), /recorded UI decision/);
    }
    assert.equal(calls.length, before);
});

test('missing native editable interface batches semantic focus and keys, without sending raw receipts or schema catalogs', async () => {
    const { gateway, calls } = fixture({ success: false, message: 'Target has no editable text interface.' });
    await gateway.call('ui_info', {});
    const observed = await gateway.call('ui_observe', { pid: 10 });
    const initialCalls = calls.length;
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: observed.elements.find(element => element.role === 'Edit').ref, text: 'literal' }] });
    assert.equal(result.success, true);
    assert.deepEqual(calls.slice(initialCalls).map(call => call.tool),
        ['list_windows', 'list_windows', 'list_windows', 'get_ui_tree', 'type_text', 'click_element', 'set_clipboard', 'hotkey', 'hotkey',
            'list_windows', 'list_windows', 'list_windows', 'get_ui_tree', 'list_windows',
            'list_windows', 'list_windows', 'get_ui_tree', 'list_windows']);
    assert.equal(calls.find(call => call.tool === 'click_element').args.snapshot_id, 'fresh');
    assert.equal(JSON.stringify(result).includes('snapshot_id'), false);
});

test('a reused PID with a different window instance cannot receive a mutation', async () => {
    const { gateway, calls } = fixture({ success: true });
    await gateway.call('ui_info', {});
    const observed = await gateway.call('ui_observe', { pid: 10 });
    gateway.windowInstances.clear();
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'click', ref: observed.elements.find(element => element.role === 'Edit').ref }] });
    assert.equal(result.success, false);
    assert.match(result.error, /replaced/);
    assert.equal(calls.some(call => call.tool === 'click_element'), false);
});

test('ambiguous identity stops the batch before any mutation', async () => {
    const { gateway, calls } = fixture({ success: true }, true);
    await gateway.call('ui_info', {});
    const observed = await gateway.call('ui_observe', { pid: 10 });
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: observed.elements.find(element => element.role === 'Edit').ref, text: 'text' }] });
    assert.equal(result.success, false);
    assert.equal(result.completed_steps, 0);
    assert.equal(calls.some(call => ['click_element', 'type_text', 'hotkey'].includes(call.tool)), false);
});

test('unknown setter failure is never retried with keyboard input; invalid later steps reject the entire batch before input', async () => {
    const { gateway, calls } = fixture({ success: false, message: 'Provider failed after dispatch.' });
    await gateway.call('ui_info', {});
    const observed = await gateway.call('ui_observe', { pid: 10 });
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: observed.elements.find(element => element.role === 'Edit').ref, text: 'text' }] });
    assert.equal(result.failed_step_may_have_changed_ui, true);
    assert.equal(calls.some(call => ['click_element', 'hotkey'].includes(call.tool)), false);
    const before = calls.length;
    await assert.rejects(gateway.call('ui_act', { pid: 10, steps: [
        { action: 'click', ref: observed.elements.find(element => element.role === 'Edit').ref }, { action: 'program_api' }
    ] }), /Unsupported/);
    assert.equal(calls.length, before);
    await assert.rejects(gateway.call('ui_observe', { pid: 11 }), /Select a PID from the current ui_info reply/);
});

test('a setter reply without explicit success is unconfirmed and never causes a keyboard retry', async () => {
    const { gateway, calls } = fixture({});
    await gateway.call('ui_info', {});
    const observed = await gateway.call('ui_observe', { pid: 10 });
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: observed.elements.find(element => element.role === 'Edit').ref, text: 'text' }] });
    assert.equal(result.success, false);
    assert.match(result.error, /unconfirmed/);
    assert.equal(calls.some(call => call.tool === 'hotkey'), false);
});

function compactFixture() {
    let value = '';
    let showNewControl = false;
    let ignoreWrite = false;
    let window = { Title: 'Fixture', WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z' };
    const calls = [];
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, ...window }];
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = '[1] Window ' + JSON.stringify(window.Title) + ' (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [2] Edit "Field" (autoId: "field", val: ' + JSON.stringify(value) + ', enabled: True, offscreen: False)';
            if (showNewControl) result.tree += '\n  [3] button "Next" (autoId: "next", val: null, enabled: True, offscreen: False)';
        }
        if (tool === 'type_text' && !ignoreWrite) value = args.text;
        if (tool === 'click_element') showNewControl = true;
        return { result: { structuredContent: result } };
    } };
    return { gateway: new DesktopUiGateway(client), calls, ignoreWrites: () => { ignoreWrite = true; },
        changeWindow: changes => { window = { ...window, ...changes }; } };
}

test('compact ui_act verifies full writes without learning and discovers private result retrieval', async () => {
    const { gateway, calls, ignoreWrites } = compactFixture();
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const text = 'x'.repeat(16000);
    const reply = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref, text }] });
    assert.equal(reply.success, true);
    assert.equal(reply.outputs.step_1.matches_input, true);
    assert.equal(reply.ui.kind, 'delta');
    assert.equal(reply.observation_required, undefined);
    assert.ok(Buffer.byteLength(JSON.stringify(reply)) < 8000);
    assert.equal((await gateway.call('cartridge_browse', { topic: 'Info' })).functions[0].name, 'Info.result');
    assert.equal((await gateway.call('cartridge_browse', { function: 'Info.result' })).function.inputSchema.properties.limit.maximum, 8000);
    const page = await gateway.call('cartridge_call', { function: 'Info.result',
        arguments: { evidence_ref: reply.evidence_ref, section: 'outputs/step_1' } });
    assert.equal(page.text, text.slice(0, 2000));
    assert.equal(page.complete, false);
    const before = calls.length;
    for (const options of [{ result_mode: 'other' }, { result_mode: 'values', fields: [] }]) {
        await assert.rejects(gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'never' }], ...options }));
    }
    assert.equal(calls.length, before);
    await assert.rejects(gateway.call('cartridge_call', { function: 'Info.scan_form', arguments: {} }), /configured for learning/);
    ignoreWrites();
    const failed = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref, text: 'ignored' }] });
    assert.equal(failed.success, false);
    assert.equal(failed.verified, false);
    assert.equal(failed.completed_steps, 0);
    assert.equal(failed.failed_step, 0);
    assert.equal(failed.failed_step_may_have_changed_ui, true);
    gateway.disconnect();
    await assert.rejects(gateway.call('cartridge_call', { function: 'Info.result',
        arguments: { evidence_ref: reply.evidence_ref, section: 'debug' } }), /evidence_not_found/);
});

test('summary navigation preserves effects and lists the newly exposed controls in its small change list', async () => {
    const { gateway, calls } = compactFixture();
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const reply = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'click', ref }] });
    assert.equal(reply.observation_required, undefined);
    assert.deepEqual(reply.unconfirmed_effects, [{ step: 1, action: 'click' }]);
    const exposed = reply.ui.changed.find(row => row.name === 'Next').ref;
    const clicked = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'click', ref: exposed }] });
    assert.equal(clicked.success, true);
});

test('a later successful write cannot hide an ignored earlier write in a compact batch', async () => {
    const { gateway } = compactFixture();
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const original = gateway.client.call;
    let writes = 0;
    gateway.client.call = async (tool, args) => {
        if (tool === 'type_text' && ++writes === 1) return { result: { structuredContent: { success: true } } };
        return original(tool, args);
    };
    const result = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [
        { action: 'write', ref, text: 'first' }, { action: 'write', ref, text: 'second' }
    ] });
    assert.equal(result.success, false);
    assert.equal(result.verified, false);
    assert.equal(result.completed_steps, 0);
    assert.equal(result.failed_step, 0);
    assert.equal(result.failed_step_may_have_changed_ui, true);
    assert.equal(writes, 1);
});

test('a verified title change keeps the same window scope and old observed control can be resolved freshly', async () => {
    const { gateway, changeWindow } = compactFixture();
    await gateway.call('ui_info', {});
    const first = await gateway.call('ui_observe', { pid: 10 });
    const ref = first.elements.find(element => element.role === 'Edit').ref;
    changeWindow({ Title: 'Renamed document' });
    const result = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref, text: 'same window' }] });
    assert.equal(result.success, true);
    assert.equal(result.outputs.step_1.matches_input, true);
    const next = await gateway.call('ui_observe', { pid: 10 });
    assert.equal(next.kind, 'delta');
    const again = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref, text: 'again' }] });
    assert.equal(again.success, true);
});

test('reused PID or unverifiable window continuity invalidates old input references', async () => {
    const { gateway, changeWindow, calls } = compactFixture();
    await gateway.call('ui_info', {});
    const first = await gateway.call('ui_observe', { pid: 10 });
    const ref = first.elements.find(element => element.role === 'Edit').ref;
    changeWindow({ ProcessStartedAtUtc: '2026-10-08T01:00:00Z' });
    const before = calls.filter(call => call.tool === 'type_text').length;
    const refused = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'never' }] });
    assert.equal(refused.success, false);
    assert.equal(calls.filter(call => call.tool === 'type_text').length, before);
    const replacement = await gateway.call('ui_observe', { pid: 10, since_view: first.view_ref });
    assert.equal(replacement.kind, 'full');
    assert.throws(() => gateway.projection.target(ref, 10), /Unknown reference/);
    changeWindow({ ProcessStartedAtUtc: undefined });
    const uncertain = await gateway.call('ui_observe', { pid: 10 });
    assert.equal(uncertain.kind, 'full');
    assert.equal(uncertain.baseline_reason, 'instance_unverified');
    const unknownRef = uncertain.elements.find(element => element.role === 'Edit').ref;
    assert.equal((await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: unknownRef, text: 'never' }] })).success, false);
});

test('a caption that changed in the observed tree does not change the handle and kind that receive input', async () => {
    const { gateway, calls } = compactFixture();
    const original = gateway.client.call;
    gateway.client.call = async (tool, args) => {
        const response = await original(tool, args);
        if (tool === 'get_ui_tree') response.result.structuredContent.tree = response.result.structuredContent.tree.replace('"Fixture"', '"Other window"');
        return response;
    };
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'never' }] });
    assert.equal(result.success, true);
    assert.doesNotMatch(String(result.error ?? ''), /unverified_window/);
    assert.equal(calls.some(call => call.tool === 'type_text'), true, 'the observed handle still receives the write');
});

const ambiguousWindowText = 'Recipe window is absent, foreign or ambiguous; name the main window with the window argument '
    + '(the native handle from ui_info).';

test('two main windows observe nothing until a call names one window, and the named window is the one read', async () => {
    const { gateway, calls, windows } = windowFixture();
    windows[1].Kind = 'main';
    await gateway.call('ui_info', {});
    await assert.rejects(gateway.call('ui_observe', { pid: 10 }), { message: ambiguousWindowText });
    assert.equal(calls.some(call => call.tool === 'get_ui_tree'), false, 'a guessed window is never read');
    await gateway.call('ui_observe', { pid: 10, window: 101 });
    assert.equal(lastTreeArguments(calls).window, 101);
});

test('each window kind of a learned function is bound once and every step reads the tree of its own kind', async () => {
    const { gateway, calls } = windowFixture();
    await gateway.call('ui_info', {});
    const entry = { steps: [{ action: 'write', selector: { role: 'Edit', name: 'Field', window: 'main' }, route: 'native-text', parameter: 'text' },
        { action: 'click', selector: { role: 'Button', name: 'Commit', window: 'dialog' }, route: 'semantic' }],
    outputs: [{ name: 'value', selector: { role: 'Edit', name: 'Field', window: 'main' }, field: 'value', check: { kind: 'exists' } }] };
    const bindings = await gateway.bindFunctionWindows(entry, 10);
    assert.equal(bindings.get('main').WindowHandle, 100);
    assert.equal(bindings.get('dialog').WindowHandle, 101);
    await gateway.readTrees(10, bindings);
    assert.deepEqual(calls.filter(call => call.tool === 'get_ui_tree').map(call => call.args.window), [100, 101]);
});

// Two windows of one process: the handle, not the caption, names each window for the node.
function windowFixture() {
    const calls = [];
    const windows = [
        { ProcessId: 10, Title: 'Fixture', WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z' },
        { ProcessId: 10, Title: 'Fixture', WindowHandle: 101, Kind: 'dialog', ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }
    ];
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        const result = { success: true, windows, capabilities: { nodeId: 'fixture-node', platform: 'linux-x11-atspi' } };
        if (tool === 'get_ui_tree') {
            result.tree = '[1] Window "Fixture" (autoId: "", val: "", enabled: True, offscreen: False)';
            result.snapshot_id = 'fresh';
        }
        return { result: { structuredContent: result } };
    } };
    return { gateway: new DesktopUiGateway(client), calls, windows };
}

function lastTreeArguments(calls) {
    return calls.filter(call => call.tool === 'get_ui_tree').at(-1).args;
}

test('ui_info names each window by its handle and kind', async () => {
    const { gateway } = windowFixture();
    const info = await gateway.call('ui_info', {});
    assert.deepEqual(info.windows.map(window => [window.window, window.kind]), [[100, 'main'], [101, 'dialog']]);
});

test('a named window reaches get_ui_tree with its process, and a window alone takes its process from the reply', async () => {
    const { gateway, calls } = windowFixture();
    await gateway.call('ui_info', {});
    await gateway.call('ui_observe', { pid: 10, window: 101 });
    assert.deepEqual([lastTreeArguments(calls).pid, lastTreeArguments(calls).window], [10, 101]);
    await gateway.call('ui_observe', { window: 100 });
    assert.deepEqual([lastTreeArguments(calls).pid, lastTreeArguments(calls).window], [10, 100]);
});

test('an unknown or mismatching window handle is refused before any node call', async () => {
    const { gateway, calls } = windowFixture();
    await gateway.call('ui_info', {});
    const before = calls.length;
    await assert.rejects(gateway.call('ui_observe', { pid: 10, window: 999 }), /window handle is not one of the current windows/);
    await assert.rejects(gateway.call('ui_observe', { pid: 11, window: 101 }), /window handle is not one of the current windows/);
    assert.equal(calls.length, before);
});

test('a remembered window keeps pid-only requests only while Info still lists it under that process', async () => {
    const { gateway, calls, windows } = windowFixture();
    await gateway.call('ui_info', {});
    await gateway.call('ui_observe', { pid: 10, window: 101 });
    await gateway.call('ui_observe', { pid: 10 });
    assert.equal(lastTreeArguments(calls).window, 101);
    windows.pop();
    await gateway.call('ui_info', {});
    await gateway.call('ui_observe', { pid: 10 });
    assert.equal(lastTreeArguments(calls).window, 100, 'the only window left for the process is named, never omitted');
});

// A control with neither a name nor an automation ID is identified only by its window and container path, so each row states its depth.
// ignoreText models a field that reports the write as dispatched while its value stays unchanged.
function unnamedFixture(rows, { ignoreText = false, noTextInterface = false } = {}) {
    const calls = [];
    const values = new Map(rows.filter(row => row.value !== undefined).map(row => [row.id, row.value]));
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: 'Fixture', WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }];
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = rows.map(row => ' '.repeat(row.depth) + '[' + row.id + '] ' + row.role + ' ' + JSON.stringify(row.name ?? '')
                + ' (autoId: "", val: ' + (values.has(row.id) ? JSON.stringify(values.get(row.id)) : 'null') + ', enabled: True, offscreen: False)')
                .join('\n');
        }
        if (tool === 'type_text' && noTextInterface) {
            result.success = false;
            result.message = 'Target has no editable text interface.';
        }
        if (tool === 'type_text' && !ignoreText && !noTextInterface) values.set(args.id, args.text);
        return { result: { structuredContent: result } };
    } };
    return { gateway: new DesktopUiGateway(client), calls, values };
}

// Two unnamed entries under different container paths, as a calculator lays out its input line and a second unnamed line.
const calculatorRows = [
    { depth: 0, id: 1, role: 'Window', name: 'Calculator' },
    { depth: 2, id: 2, role: 'Panel' },
    { depth: 4, id: 3, role: 'Scroll pane' },
    { depth: 6, id: 4, role: 'Edit', value: '0' },
    { depth: 2, id: 5, role: 'Panel' },
    { depth: 4, id: 6, role: 'Panel' },
    { depth: 6, id: 7, role: 'Edit', value: '' }
];

test('two unnamed text entries in different container paths: a write reaches only the entry its reference names, and its read-back verifies it', async () => {
    const { gateway, calls, values } = unnamedFixture(calculatorRows);
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const first = view.elements.find(element => element.role === 'Edit' && element.value === '0');
    const result = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref: first.ref, text: '6*7' }] });
    assert.equal(result.success, true);
    assert.equal(result.outputs.step_1.verified, true);
    assert.equal(values.get(4), '6*7');
    assert.equal(values.get(7), '');
    assert.deepEqual(calls.filter(call => call.tool === 'type_text').map(call => call.args.id), [4]);
});

test('two unnamed text entries with one container path are ambiguous, so the write is refused before any input', async () => {
    const { gateway, calls } = unnamedFixture([
        { depth: 0, id: 1, role: 'Window', name: 'Calculator' },
        { depth: 2, id: 2, role: 'Edit', value: '0' },
        { depth: 2, id: 3, role: 'Edit', value: '' }
    ]);
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: '6*7' }] });
    assert.equal(result.success, false);
    assert.match(result.error, /ambiguous/);
    assert.equal(calls.some(call => call.tool === 'type_text'), false);
});

test('an unnamed button is never clicked or written, and its refusal names the unnamed-target rule', async () => {
    const { gateway, calls } = unnamedFixture([
        { depth: 0, id: 1, role: 'Window', name: 'Calculator' },
        { depth: 2, id: 2, role: 'Button' }
    ]);
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Button').ref;
    for (const step of [{ action: 'click', ref }, { action: 'write', ref, text: '6*7' }]) {
        const result = await gateway.call('ui_act', { pid: 10, steps: [step] });
        assert.equal(result.success, false);
        assert.equal(result.error, unnamedTargetRefusal);
    }
    assert.equal(calls.some(call => ['click_element', 'type_text'].includes(call.tool)), false);
});

test('an unnamed text entry whose read-back stays unchanged fails as a written-value mismatch with verified false and no keyboard retry', async () => {
    const { gateway, calls } = unnamedFixture(calculatorRows, { ignoreText: true });
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const first = view.elements.find(element => element.role === 'Edit' && element.value === '0');
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: first.ref, text: '6*7' }] });
    assert.equal(result.success, false);
    assert.equal(result.verified, false);
    assert.equal(result.error, 'The observed value does not match the written input.');
    assert.equal(calls.filter(call => call.tool === 'type_text').length, 1);
    assert.equal(calls.some(call => ['set_clipboard', 'hotkey'].includes(call.tool)), false);
});

test('an unnamed control is refused before any native call unless it is a write bound to a window and a container path', async () => {
    const { gateway, calls } = unnamedFixture(calculatorRows);
    await gateway.call('ui_info', {});
    const before = calls.length;
    const bound = { role: 'Edit', name: '', autoId: '', windowKind: 'main', containerPath: [] };
    await assert.rejects(gateway.resolveIdentity(10, { ...bound, windowKind: undefined }, false, undefined, 'write'),
        { message: unnamedTargetRefusal });
    await assert.rejects(gateway.resolveIdentity(10, { ...bound, containerPath: undefined }, false, undefined, 'write'),
        { message: unnamedTargetRefusal });
    await assert.rejects(gateway.performIdentity(10, { action: 'select', text: 'x' }, { ...bound, role: 'combo box' }),
        { message: unnamedTargetRefusal });
    assert.equal(calls.length, before);
    assert.equal(unnamedWriteAllowed(bound, 'write'), true);
    assert.equal(unnamedWriteAllowed({ ...bound, windowKind: undefined, windowHandle: 100 }, 'write'), true);
});

test('an unnamed text area directly under its window takes a written value, and only its read-back decides success', async () => {
    const rows = [
        { depth: 0, id: 1, role: 'Window', name: 'TextEdit' },
        { depth: 2, id: 2, role: 'TextArea', value: '' }
    ];
    const { gateway, values } = unnamedFixture(rows);
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const area = view.elements.find(element => element.role === 'TextArea');
    const written = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref: area.ref, text: 'Hello' }] });
    assert.equal(written.success, true);
    assert.equal(written.outputs.step_1.verified, true);
    assert.equal(values.get(2), 'Hello');

    const silent = unnamedFixture(rows, { ignoreText: true });
    await silent.gateway.call('ui_info', {});
    const silentView = await silent.gateway.call('ui_observe', { pid: 10 });
    const silentArea = silentView.elements.find(element => element.role === 'TextArea');
    const refused = await silent.gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: silentArea.ref, text: 'Hello' }] });
    assert.equal(refused.success, false);
    assert.equal(refused.verified, false);
});

test('an unnamed text entry with no window row above it has no proven container path, so its write is refused', async () => {
    const { gateway, calls } = unnamedFixture([{ depth: 0, id: 1, role: 'TextArea', value: '' }]);
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const area = view.elements.find(element => element.role === 'TextArea');
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: area.ref, text: 'Hello' }] });
    assert.equal(result.success, false);
    assert.equal(result.error, unnamedTargetRefusal);
    assert.equal(calls.some(call => call.tool === 'type_text'), false);
});

test('an unnamed text field whose native write reports no text interface is refused, with no click, hotkey or clipboard call', async () => {
    const { gateway, calls, values } = unnamedFixture(calculatorRows, { noTextInterface: true });
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const first = view.elements.find(element => element.role === 'Edit' && element.value === '0');
    const result = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: first.ref, text: '6*7' }] });
    assert.equal(result.success, false);
    assert.equal(result.error, unnamedTextFieldRefusal);
    assert.equal(values.get(4), '0');
    assert.equal(calls.filter(call => call.tool === 'type_text').length, 1);
    assert.equal(calls.some(call => ['click_element', 'set_clipboard', 'hotkey'].includes(call.tool)), false);
    const before = calls.length;
    await assert.rejects(gateway.performIdentity(10, { action: 'write', text: '6*7' },
        { role: 'Edit', name: '', autoId: '', windowKind: 'main', containerPath: [] },
        'semantic-focus+keyboard (native editable interface unavailable)'), { message: unnamedTextFieldRefusal });
    assert.equal(calls.length, before, 'a recorded keyboard route for an unnamed field is refused before any native call');
});

// Each click_element call answers with the next queued reply; the tree holds one named button.
function clickFixture(clickReplies) {
    const client = { call: async (tool, args) => {
        let result = { success: true };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: 'Fixture', WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }];
        if (tool === 'get_ui_tree') {
            result.tree = '[1] Window "Fixture" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [99] Button "Save" (autoId: "", val: "", enabled: True, offscreen: False)';
            result.snapshot_id = 'fresh';
        }
        if (tool === 'click_element') result = clickReplies.shift() ?? { success: true, outcome: 'completed' };
        return { result: { structuredContent: result } };
    } };
    return new DesktopUiGateway(client);
}

async function saveButtonRef(gateway) {
    await gateway.call('ui_info', {});
    return (await gateway.call('ui_observe', { pid: 10 })).elements.find(element => element.name === 'Save').ref;
}

test('a refused click says nothing changed only when the node proves it refused before dispatch', async () => {
    const proven = clickFixture([{ success: false, outcome: 'not_dispatched', error: 'Policy refused the click.' }]);
    const provenReply = await proven.call('ui_act', { pid: 10, steps: [{ action: 'click', ref: await saveButtonRef(proven) }] });
    assert.equal(provenReply.success, false);
    assert.equal(provenReply.completed_steps, 0);
    assert.equal(provenReply.failed_step_may_have_changed_ui, false);

    const unproven = clickFixture([{ success: false, error: 'Connection lost after the click was sent.' }]);
    const unprovenReply = await unproven.call('ui_act', { pid: 10, steps: [{ action: 'click', ref: await saveButtonRef(unproven) }] });
    assert.equal(unprovenReply.failed_step_may_have_changed_ui, true);
});

test('a final observation that fails after a finished click has no failed step and reports the phase after_steps', async () => {
    let clicked = false;
    const client = { call: async (tool) => {
        let result = { success: true };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: 'Fixture', WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }];
        if (tool === 'get_ui_tree') {
            result.tree = '[1] Window "Fixture" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [99] Button "Save" (autoId: "", val: "", enabled: True, offscreen: False)';
            result.snapshot_id = 'fresh';
            result.truncated = clicked;
        }
        if (tool === 'click_element') clicked = true;
        return { result: { structuredContent: result } };
    } };
    const gateway = new DesktopUiGateway(client);
    await gateway.call('ui_info', {});
    const ref = (await gateway.call('ui_observe', { pid: 10 })).elements.find(element => element.name === 'Save').ref;
    const reply = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'click', ref }] });
    assert.equal(reply.success, false);
    assert.equal(reply.completed_steps, 1);
    assert.equal(Object.hasOwn(reply, 'failed_step'), false);
    assert.equal(reply.phase, 'after_steps');
    assert.equal(reply.failed_step_may_have_changed_ui, true);
    assert.match(reply.error, /incomplete/);
});

test('a refusal of a later step leaves the finished step counted and names the refused step, which sent nothing', async () => {
    const gateway = clickFixture([{ success: true, outcome: 'completed' }, { success: false, outcome: 'not_dispatched', error: 'Refused.' }]);
    const ref = await saveButtonRef(gateway);
    const reply = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'click', ref }, { action: 'click', ref }] });
    assert.equal(reply.completed_steps, 1);
    assert.equal(reply.failed_step, 1);
    assert.equal(reply.failed_step_may_have_changed_ui, false);
});

function keyFixture() {
    const client = { call: async (tool, args) => {
        let result = { success: true };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: 'Fixture', WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }];
        if (tool === 'hotkey') result = { success: false, error: 'Unsupported key: ' + args.keys.join('+'), outcome: 'not_dispatched' };
        return { result: { structuredContent: result } };
    } };
    return new DesktopUiGateway(client);
}

test('a key chord written as one string is refused with the separate-key hint; other refused keys keep their message', async () => {
    const gateway = keyFixture();
    await gateway.call('ui_info', {});
    const chord = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'key', keys: ['CTRL+SHIFT+S'] }] });
    assert.equal(chord.success, false);
    assert.equal(chord.error, 'Unsupported key: CTRL+SHIFT+S. Name each key separately, for example ["ctrl", "shift", "s"].');
    const punctuated = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'key', keys: ['A+B.'] }] });
    assert.equal(punctuated.error, 'Unsupported key: A+B. Name each key separately, for example ["ctrl", "shift", "s"].');
    const single = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'key', keys: ['NOSUCH'] }] });
    assert.equal(single.error, 'Unsupported key: NOSUCH');
});

test('a key step with a ref focuses its element without activating it before the keys; without a ref nothing is clicked', async () => {
    const calls = [];
    const inner = clickFixture([]);
    const recording = new DesktopUiGateway({ call: async (tool, args) => {
        calls.push({ tool, args });
        return inner.client.call(tool, args);
    } });
    const ref = await saveButtonRef(recording);
    calls.length = 0;
    const focused = await recording.call('ui_act', { pid: 10, steps: [{ action: 'key', keys: ['enter'], ref }] });
    assert.equal(focused.success, true);
    const actions = calls.map(call => call.tool).filter(tool => ['focus_window', 'click_element', 'hotkey'].includes(tool));
    assert.deepEqual(actions, ['focus_window', 'click_element', 'hotkey']);
    assert.equal(calls.find(call => call.tool === 'click_element').args.focus_only, true);
    calls.length = 0;
    await recording.call('ui_act', { pid: 10, steps: [{ action: 'key', keys: ['enter'] }] });
    assert.deepEqual(calls.map(call => call.tool).filter(tool => ['focus_window', 'click_element', 'hotkey'].includes(tool)),
        ['focus_window', 'hotkey']);
});

// A closed Edit menu keeps its items offscreen beneath one onscreen item, as an application reports them until the menu opens.
function closedMenuFixture() {
    const client = { call: async tool => {
        const result = { success: true };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: 'Editor', WindowHandle: 100, Kind: 'main',
            ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }];
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = '[1] Window "Editor" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [2] menu item "Edit" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '    [3] menu item "Select All" (autoId: "", val: "", enabled: True, offscreen: True)\n'
                + '    [4] menu item "Convert" (autoId: "", val: "", enabled: True, offscreen: True)\n'
                + '      [5] menu item "To Uppercase" (autoId: "", val: "", enabled: True, offscreen: True)\n'
                + '  [6] Edit "Field" (autoId: "", val: "old", enabled: True, offscreen: False)';
        }
        return { result: { structuredContent: result } };
    } };
    return new DesktopUiGateway(client);
}

test('a default full view omits closed menu items, counts them, and lists them for contains or offscreen:true', async () => {
    const gateway = closedMenuFixture();
    await gateway.call('ui_info', {});
    const lean = await gateway.call('ui_observe', { pid: 10 });
    assert.deepEqual(lean.elements.map(element => element.name), ['Editor', 'Edit', 'Field']);
    assert.equal(lean.offscreen_omitted, 3);
    assert.equal(lean.hint, 'Pass contains or offscreen:true to include closed menus and hidden elements.');
    const searched = await gateway.call('ui_observe', { pid: 10, contains: 'Upper' });
    assert.deepEqual(searched.elements.map(element => [element.name, element.offscreen]), [['To Uppercase', true]]);
    assert.equal(searched.offscreen_omitted, undefined);
    const byValue = await gateway.call('ui_observe', { pid: 10, contains: 'old' });
    assert.ok(byValue.elements.some(element => element.role === 'Edit' && element.name === 'Field'));
    const everything = await gateway.call('ui_observe', { pid: 10, offscreen: true });
    assert.equal(everything.elements.filter(element => element.offscreen).length, 3);
    assert.equal(everything.offscreen_omitted, undefined);
    assert.equal(everything.hint, undefined);
});

test('ui_observe as state reads the window as a typed class', async () => {
    const save = '\n  [2] Button "Save" (autoId: "save", val: null, enabled: True, offscreen: False)';
    const { gateway } = fixture({ success: true }, false, save);
    const info = await gateway.call('ui_info', {});
    assert.match(info.next, /as: "state"/);
    const state = await gateway.call('ui_observe', { pid: 10, as: 'state' });
    assert.equal(state.success, true);
    assert.match(state.declaration, /\(\): void;$/m);
    assert.equal(state.shape.length, 16);
    const missing = await gateway.call('ui_observe', { pid: 10, as: 'state', cursor: 'nothing@0' });
    assert.equal(missing.error, 'unknown_part');
    await assert.rejects(gateway.call('ui_observe', { pid: 10, as: 'state', contains: 'Save' }), /accepts pid, window, cursor and max_chars/);
});
