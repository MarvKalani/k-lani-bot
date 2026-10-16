import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DesktopUiGateway, ambiguousWindowRefusal, demonstrationWindowRefusal, referenceWindowRefusal,
    uniqueWindowRefusal } from './gateway.mjs';
import { LearnedFunctions } from './learned-functions.mjs';

function fixture(directory, windowRows = null) {
    let listed = windowRows ?? [{ ProcessId: 10, WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z', Title: 'Editor' }];
    let value = '';
    let dropWrite = false;
    let appendWrite = false;
    let emptyWrite = false;
    let control = { role: 'Edit', name: 'Document', autoId: 'document', enabled: true, present: true, duplicate: false };
    let saveControl = null;
    const calls = [];
    const configuration = { cartridgeRoot: directory, auto_capture: false,
        application: { key: 'fixture-editor', version: '1.0.0', identity_source: 'controlled-test-fixture' } };
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = listed;
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = '[1] Window "Editor" (autoId: "", val: "", enabled: True, offscreen: False)';
            if (control.present) {
                const row = ' ' + control.role + ' ' + JSON.stringify(control.name) + ' (autoId: ' + JSON.stringify(control.autoId)
                    + ', val: ' + JSON.stringify(value) + ', enabled: ' + (control.enabled ? 'True' : 'False') + ', offscreen: False)';
                result.tree += '\n  [2]' + row;
                if (control.duplicate) result.tree += '\n  [3]' + row;
            }
            if (saveControl) {
                const saveEnabled = !saveControl.disabled && (!saveControl.requiresEdit || value !== '');
                result.tree += '\n  [4] Button "Save" (autoId: "save", val: "", enabled: ' + (saveEnabled ? 'True' : 'False') + ', offscreen: False)';
            }
        }
        if (tool === 'type_text' && !dropWrite) value = emptyWrite ? '' : appendWrite ? value + args.text : args.text;
        return { result: { structuredContent: result } };
    } };
    function gateway() {
        const instance = new DesktopUiGateway(client, () => {}, configuration);
        instance.serverInfo = { name: 'node', version: '0.2610.0609' };
        return instance;
    }
    return { gateway, calls, ignoreWrites: () => { dropWrite = true; }, configuration, setWindows: rows => { listed = rows; },
        appendWrites: () => { appendWrite = true; }, emptyWrites: () => { emptyWrite = true; },
        setValue: text => { value = text; },
        changeControl: changes => { control = { ...control, ...changes }; },
        addSave: ({ requiresEdit = false }) => { saveControl = { requiresEdit, disabled: false }; },
        disableSave: () => { saveControl.disabled = true; } };
}

async function teach(gateway, text) {
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const demonstration = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text }] });
    const args = { evidence: demonstration.evidence, name: 'editor.write_text', topic: 'Use/Editor', parameters: ['text'],
        outputs: [{ name: 'content', ref, field: 'value' }] };
    return gateway.call('cartridge_call', { function: 'Programming.learn', arguments: args });
}

test('successful observation generates a parameterized function, hot-discovers it, and survives a new gateway instance', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-learned-functions-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    const first = setup.gateway();
    const learned = await teach(first, 'PRIVATE_TRAINING_VALUE');
    assert.equal(learned.success, true);
    assert.equal((await first.call('cartridge_browse', { topic: 'Use/Editor' })).functions.length, 1);
    const stored = fs.readFileSync(first.learned.file, 'utf8');
    assert.equal(stored.includes('PRIVATE_TRAINING_VALUE'), false);
    const definition = JSON.parse(stored).functions[0];
    assert.equal(definition.steps[0].selector.pid, undefined);
    assert.equal(definition.steps[0].selector.id, undefined);
    assert.equal(definition.steps[0].selector.ref, undefined);
    const second = setup.gateway();
    await second.call('ui_info', {});
    const reply = await second.call('cartridge_call', { function: 'editor.write_text', arguments: { pid: 10, parameters: { text: 'SECOND_VALUE' } } });
    assert.equal(reply.verified, true);
    assert.deepEqual(reply.outputs, { content: 'SECOND_VALUE' });
    assert.deepEqual(reply.steps, ['write Document']);
    assert.equal(reply.ui.kind, 'delta');
    assert.equal(reply.images, undefined);
});

test('first ordinary write automatically returns a reusable function without a Programming.learn call', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-auto-capture-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    setup.configuration.auto_capture = true;
    const gateway = setup.gateway();
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const first = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'PRIVATE_FIRST_USE' }] });
    assert.deepEqual(first.mcp, { functions: [] });
    assert.equal(fs.readFileSync(gateway.learned.file, 'utf8').includes('PRIVATE_FIRST_USE'), false);
    const generated = await gateway.call('cartridge_browse', { topic: 'Use/UI' });
    assert.equal(generated.functions.length, 1);
    const replay = await gateway.call('cartridge_call', { function: generated.functions[0].name,
        arguments: { pid: 10, parameters: { text: 'second access' } } });
    assert.equal(replay.verified, true);
    assert.deepEqual(replay.outputs, { value: 'second access' });
});

test('unverified first-use material is persisted as a candidate but cannot be called as a verified function', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-auto-candidate-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    setup.configuration.auto_capture = true;
    const gateway = setup.gateway();
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const reply = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'click', ref }] });
    const candidate = reply.mcp.functions[0];
    assert.equal(candidate.status, 'needs_verification');
    assert.equal(candidate.function, undefined);
    const restarted = setup.gateway();
    const persisted = restarted.learned.candidates[0].name;
    await assert.rejects(restarted.call('cartridge_call', { function: persisted, arguments: {} }), /Unknown generated function/);
});

test('dispatch without an observed effect cannot be learned, and a learned call never reports success after an ignored write', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-learned-failure-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    const gateway = setup.gateway();
    await teach(gateway, 'first');
    setup.ignoreWrites();
    const reply = await gateway.call('cartridge_call', { function: 'editor.write_text', arguments: { pid: 10, parameters: { text: 'not written' } } });
    assert.equal(reply.success, false);
    assert.equal(reply.verified, false);
    const next = setup.gateway();
    await assert.rejects(teach(next, 'not written'), /The evidence token is missing or unknown; pass the evidence value from the latest successful ui_act reply\./);
});

for (const incorrect of ['append', 'empty']) {
    test('a learned writer refuses ' + incorrect + ' even when its declared output only checks existence', async context => {
        const { setup, gateway } = await trainedFixture(context);
        gateway.learned.get('editor.write_text').outputs[0].check = { kind: 'exists' };
        if (incorrect === 'append') setup.appendWrites();
        else setup.emptyWrites();
        const result = await executeWrite(gateway);
        assert.equal(result.success, false);
        assert.equal(result.verified, false);
        assert.match(result.error, /The observed value does not match the written input\./);
        assert.equal(setup.calls.filter(call => call.tool === 'type_text').length, 1);
    });
}

test('a changed node build fails compatibility before input', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-learned-version-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    const gateway = setup.gateway();
    await teach(gateway, 'first');
    const before = setup.calls.length;
    gateway.serverInfo.version = 'different-build';
    await assert.rejects(gateway.call('cartridge_call', { function: 'editor.write_text',
        arguments: { pid: 10, parameters: { text: 'not written' } } }), /compatibility changed/);
    assert.equal(setup.calls.length, before);
});

async function trainedFixture(context) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-preflight-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    const gateway = setup.gateway();
    await teach(gateway, 'first');
    setup.calls.length = 0;
    return { setup, gateway };
}

const executeWrite = gateway => gateway.call('cartridge_call', { function: 'editor.write_text',
    arguments: { pid: 10, parameters: { text: 'next value' } } });

test('a 16000-character known write is fully verified while summary returns less than half the legacy bytes', async context => {
    const { setup, gateway } = await trainedFixture(context);
    const text = 'ä'.repeat(16000);
    const argumentsForWrite = { pid: 10, parameters: { text } };
    const legacy = await gateway.call('cartridge_call', { function: 'editor.write_text', arguments: argumentsForWrite });
    const summary = await gateway.call('cartridge_call', { function: 'editor.write_text',
        arguments: { ...argumentsForWrite, result_mode: 'summary' } });
    assert.equal(summary.success, true);
    assert.equal(summary.outputs.content.verified, true);
    assert.equal(summary.outputs.content.matches_input, true);
    assert.equal(summary.outputs.content.utf8_length, Buffer.byteLength(text));
    assert.equal(JSON.stringify(summary).includes(text), false);
    assert.ok(Buffer.byteLength(JSON.stringify(summary)) <= Buffer.byteLength(JSON.stringify(legacy)) / 2);
    context.diagnostic(JSON.stringify({ legacy_result_bytes: Buffer.byteLength(JSON.stringify(legacy)),
        summary_result_bytes: Buffer.byteLength(JSON.stringify(summary)), verified_utf8_bytes: Buffer.byteLength(text) }));
    setup.ignoreWrites();
    const failed = await gateway.call('cartridge_call', { function: 'editor.write_text',
        arguments: { pid: 10, parameters: { text: text.slice(0, -1) + 'x' }, result_mode: 'summary' } });
    assert.equal(failed.success, false);
    assert.equal(failed.verified, false);
});

test('unknown long outputs have complete Unicode pages; requested fields and debug are bounded', async context => {
    const { setup, gateway } = await trainedFixture(context);
    const entry = gateway.learned.get('editor.write_text');
    entry.steps[0] = { ...entry.steps[0], action: 'click', route: 'semantic' };
    entry.outputs[0].check = { kind: 'exists' };
    const text = 'Größe😀\n'.repeat(4000);
    setup.setValue(text);
    const args = { pid: 10, parameters: { text: '' }, result_mode: 'values', fields: ['content'] };
    const reply = await gateway.call('cartridge_call', { function: entry.name, arguments: args });
    assert.equal(reply.success, true);
    assert.equal(reply.outputs.content.truncated, true);
    assert.equal(reply.outputs.content.matches_input, undefined);
    assert.equal(Array.from(reply.outputs.content.text).length, 2000);
    let actual = '';
    let offset = 0;
    do {
        const page = await gateway.call('cartridge_call', { function: 'Info.result', arguments: {
            evidence_ref: reply.evidence_ref, section: 'outputs/content', offset } });
        actual += page.text;
        offset = page.next_offset;
        if (page.complete) break;
    } while (true);
    assert.equal(actual, text);
    const debug = await gateway.call('cartridge_call', { function: entry.name, arguments: { ...args, fields: undefined, result_mode: 'debug' } });
    assert.equal(debug.truncated, true);
    assert.ok(Buffer.byteLength(JSON.stringify(debug)) <= 8192);
    const empty = await gateway.call('cartridge_call', { function: entry.name, arguments: { ...args, fields: [] } });
    assert.deepEqual(empty.outputs, {});
    const loaded = await gateway.call('cartridge_browse', { function: entry.name });
    assert.deepEqual(loaded.function.inputSchema.properties.result_mode.enum, ['summary', 'values', 'debug']);
    assert.equal(loaded.function.inputSchema.properties.fields.maxItems, 20);
    assert.equal(fs.readFileSync(gateway.learned.file, 'utf8').includes(text), false);
});

test('invalid result options are rejected before any generated input', async context => {
    const { setup, gateway } = await trainedFixture(context);
    for (const options of [{ result_mode: 'other' }, { result_mode: null }, { fields: ['content'] },
        { result_mode: 'summary', fields: [] }, { result_mode: 'values', fields: ['unknown'] },
        { result_mode: 'values', fields: ['content', 'content'] }, { result_mode: 'values', fields: Array(21).fill('content') },
        { result_mode: 'values', fields: 'content' }]) {
        await assert.rejects(gateway.call('cartridge_call', { function: 'editor.write_text',
            arguments: { pid: 10, parameters: { text: 'never sent' }, ...options } }), /Invalid/);
    }
    await assert.rejects(gateway.call('cartridge_call', { function: 'editor.write_text',
        arguments: { pid: 10, parameters: { text: 'never sent', result_mode: 'summary' } } }), /schema/);
    assert.equal(setup.calls.length, 0);
});

test('compact automatic capture names only newly generated functions and preserves schema discovery', async context => {
    const { setup, gateway } = await trainedFixture(context);
    gateway.automaticCapture = true;
    const view = await gateway.call('ui_observe', { pid: 10, full: true });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const first = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref, text: 'new' }] });
    assert.deepEqual(first.mcp, { functions: [] });
    const [{ name }] = (await gateway.call('cartridge_browse', { topic: 'Use/UI' })).functions;
    const next = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'write', ref, text: 'another' }] });
    assert.deepEqual(next.mcp, { functions: [] });
    assert.equal((await gateway.call('cartridge_browse', { function: name })).function.name, name);
    assert.equal(fs.readFileSync(gateway.learned.file, 'utf8').includes('another'), false);
});

test('120 generated functions are discoverable exactly once and changed libraries invalidate cursors', async context => {
    const { gateway } = await trainedFixture(context);
    const original = gateway.learned.get('editor.write_text');
    for (let index = 0; index < 119; index++) {
        gateway.learned.install({ ...structuredClone(original), name: 'editor.function_' + index });
    }
    let reply = await gateway.call('cartridge_browse', { topic: 'Use/Editor' });
    const names = reply.functions.map(entry => entry.name);
    while (reply.next_cursor) {
        reply = await gateway.call('cartridge_browse', { topic: 'Use/Editor', cursor: reply.next_cursor });
        names.push(...reply.functions.map(entry => entry.name));
    }
    assert.equal(names.length, 120);
    assert.equal(new Set(names).size, 120);
    const pending = await gateway.call('cartridge_browse', { topic: 'Use/Editor' });
    await assert.rejects(gateway.call('cartridge_browse', { topic: 'Info', cursor: pending.next_cursor }), /stale_cursor/);
    const document = JSON.parse(fs.readFileSync(gateway.learned.file, 'utf8'));
    document.revision++;
    fs.writeFileSync(gateway.learned.file, JSON.stringify(document));
    await assert.rejects(gateway.call('cartridge_browse', { topic: 'Use/Editor', cursor: pending.next_cursor }), /stale_cursor/);
});

test('a stored selector that names its owning window by caption is refused, because only a window kind identifies a window', context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-stored-caption-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const library = new LearnedFunctions({ cartridgeRoot: directory,
        application: { key: 'fixture-editor', version: '1.0.0', identity_source: 'controlled-test-fixture' } });
    const entry = { name: 'editor.save', topic: 'Use/Editor', validation: 'demonstrated_once', compatibility: { node_version: '0.2610.0609' },
        inputSchema: { type: 'object', properties: { parameters: { type: 'object', properties: {}, required: [], additionalProperties: false } } },
        steps: [{ action: 'click', selector: { role: 'Button', name: 'Save', windowTitle: 'Editor', containerPath: [] }, route: 'semantic' }],
        outputs: [{ name: 'value', selector: { role: 'Edit', name: 'Field', window: 'main' }, field: 'value', check: { kind: 'exists' } }] };
    assert.throws(() => library.install(entry), /owning window/);
});

test('automatic names and existing callable definitions survive caption changes under a stable ID', async context => {
    const { setup, gateway } = await trainedFixture(context);
    gateway.automaticCapture = true;
    const firstView = await gateway.call('ui_observe', { pid: 10 });
    const ref = firstView.changed?.find(element => element.role === 'Edit')?.ref
        ?? [...gateway.projection.references].find(([, target]) => target.role === 'Edit')[0];
    const first = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'before' }] });
    assert.deepEqual(first.mcp, { functions: [] });
    const [{ name }] = (await gateway.call('cartridge_browse', { topic: 'Use/UI' })).functions;
    setup.changeControl({ name: 'Dokument' });
    const view = await gateway.call('ui_observe', { pid: 10 });
    assert.equal(view.changed.find(element => element.name === 'Dokument').ref, ref);
    const next = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'after' }] });
    assert.deepEqual(next.mcp, { functions: [] });
    const listed = await gateway.call('cartridge_browse', { topic: 'Use/UI' });
    assert.deepEqual(listed.functions.map(entry => entry.name), [name], 'the stable ID keeps one definition');
    assert.equal((await gateway.call('cartridge_call', { function: name, arguments: { pid: 10, parameters: { text: 'reuse' } } })).verified, true);
});

test('read-only preflight accepts renamed captions with a stable ID, and action/output use the same identity', async context => {
    const { setup, gateway } = await trainedFixture(context);
    setup.changeControl({ name: 'Dokument' });
    const schema = await gateway.call('cartridge_browse', { function: 'Info.preflight' });
    assert.equal(schema.function.inputSchema.additionalProperties, false);
    const report = await gateway.call('cartridge_call', { function: 'Info.preflight',
        arguments: { pid: 10, function: 'editor.write_text' } });
    assert.equal(report.ready, true);
    assert.equal(report.input_sent, false);
    assert.equal(report.targets[0].name_changed, true);
    assert.deepEqual(setup.calls.map(call => call.tool), ['list_windows', 'list_windows', 'list_windows', 'get_ui_tree']);
    const result = await executeWrite(gateway);
    assert.equal(result.verified, true);
    assert.deepEqual(result.outputs, { content: 'next value' });
});

// Demonstrates a write followed by a save that was disabled before the write, then compiles the two steps into one function.
async function teachWriteAndSave(gateway) {
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const field = view.elements.find(element => element.role === 'Edit').ref;
    const save = view.elements.find(element => element.name === 'Save').ref;
    const demonstration = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref: field, text: 'NEW' }, { action: 'click', ref: save }] });
    const args = { evidence: demonstration.evidence, name: 'editor.save_after_write', topic: 'Use/Editor', parameters: ['text'],
        outputs: [{ name: 'content', ref: field, field: 'value' }] };
    return gateway.call('cartridge_call', { function: 'Programming.learn', arguments: args });
}

test('a save disabled before the demonstration is recorded as enabled by its write and runs on a clean document', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-enabled-before-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    setup.addSave({ requiresEdit: true });
    const first = setup.gateway();
    assert.equal((await teachWriteAndSave(first)).success, true);
    const stored = JSON.parse(fs.readFileSync(first.learned.file, 'utf8')).functions[0];
    assert.deepEqual(stored.steps.map(step => step.enabledBeforeFirstAction), [undefined, false]);
    setup.setValue('');
    const second = setup.gateway();
    await second.call('ui_info', {});
    const reply = await second.call('cartridge_call', { function: 'editor.save_after_write', arguments: { pid: 10, parameters: { text: 'SECOND' } } });
    assert.equal(reply.verified, true);
    assert.deepEqual(reply.outputs, { content: 'SECOND' });
});

test('a save that stays disabled after its enabling write is refused before its own input', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-enabled-before-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    setup.addSave({ requiresEdit: true });
    assert.equal((await teachWriteAndSave(setup.gateway())).success, true);
    setup.setValue('');
    setup.disableSave();
    const clicksBefore = setup.calls.filter(call => call.tool === 'click_element').length;
    const second = setup.gateway();
    await second.call('ui_info', {});
    const result = await second.call('cartridge_call', { function: 'editor.save_after_write', arguments: { pid: 10, parameters: { text: 'SECOND' } } });
    assert.equal(result.success, false);
    assert.equal(result.completed_steps, 1);
    assert.equal(result.error, 'Target is disabled. No input was sent.');
    assert.equal(setup.calls.filter(call => call.tool === 'click_element').length, clicksBefore);
});

test('a leading click keeps the strict preflight even when its target was disabled before the demonstration', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-leading-click-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory);
    setup.addSave({ requiresEdit: true });
    const gateway = setup.gateway();
    await gateway.call('ui_info', {});
    const field = (await gateway.call('ui_observe', { pid: 10 })).elements.find(element => element.role === 'Edit').ref;
    const before = (await gateway.readTree(10)).nodes;
    const after = before.map(node => node.role === 'Edit' ? { ...node, value: 'CHANGED' } : node);
    const save = { role: 'Button', name: 'Save', autoId: 'save', windowKind: 'main', containerPath: [] };
    gateway.demonstration = { evidence: 'leading-click', pid: 10, steps: [{ action: 'click' }], bindings: [save], routes: ['semantic'],
        before, after };
    const learned = await gateway.learnFunction({ evidence: 'leading-click', name: 'editor.save_first', topic: 'Use/Editor', parameters: [],
        outputs: [{ name: 'content', ref: field, field: 'value' }] });
    assert.equal(learned.success, true);
    const stored = JSON.parse(fs.readFileSync(gateway.learned.file, 'utf8')).functions.find(entry => entry.name === 'editor.save_first');
    assert.equal(Object.hasOwn(stored.steps[0], 'enabledBeforeFirstAction'), false);
    const reloaded = setup.gateway();
    await reloaded.call('ui_info', {});
    assert.equal((await reloaded.call('cartridge_browse', { function: 'editor.save_first', window: 100 })).function.name, 'editor.save_first');
});

test('a missing second target prevents the first mutation of the whole function', async context => {
    const { setup, gateway } = await trainedFixture(context);
    const entry = gateway.learned.get('editor.write_text');
    entry.steps.push({ action: 'click', selector: { role: 'button', name: 'Save', autoId: 'save', window: 'main' }, route: 'semantic' });
    const result = await executeWrite(gateway);
    assert.equal(result.success, false);
    assert.equal(result.phase, 'preflight');
    assert.equal(result.input_sent, false);
    assert.equal(result.completed_steps, 0);
    assert.match(result.error, /step 2/);
    assert.deepEqual(setup.calls.map(call => call.tool), ['list_windows', 'list_windows', 'list_windows', 'get_ui_tree']);
});

for (const [description, changes, error] of [
    ['wrong role', { role: 'button' }, /type changed/],
    ['ambiguous ID', { duplicate: true }, /ambiguous/],
    ['disabled target', { enabled: false }, /disabled/],
    ['missing target', { present: false }, /absent/],
    ['changed ID with same role/name', { autoId: 'replacement' }, /absent/]
]) {
    test('automatic preflight refuses ' + description + ' without sending input', async context => {
        const { setup, gateway } = await trainedFixture(context);
        setup.changeControl(changes);
        const result = await executeWrite(gateway);
        assert.equal(result.success, false);
        assert.equal(result.phase, 'preflight');
        assert.equal(result.input_sent, false);
        assert.match(result.error, error);
        assert.deepEqual(setup.calls.map(call => call.tool), ['list_windows', 'list_windows', 'list_windows', 'get_ui_tree']);
    });
}

test('a name-only selector does not fall back to a matching type', async context => {
    const { setup, gateway } = await trainedFixture(context);
    gateway.learned.get('editor.write_text').steps[0].selector.autoId = '';
    setup.changeControl({ name: 'Other document' });
    const result = await executeWrite(gateway);
    assert.equal(result.input_sent, false);
    assert.match(result.error, /absent/);
    assert.equal(setup.calls.some(call => call.tool === 'type_text'), false);
});

test('a target disappearing after successful preflight is rechecked before input', async context => {
    const { setup, gateway } = await trainedFixture(context);
    const originalCall = gateway.client.call;
    let trees = 0;
    gateway.client.call = async (tool, args) => {
        if (tool === 'get_ui_tree' && ++trees === 2) setup.changeControl({ present: false });
        return originalCall(tool, args);
    };
    const result = await executeWrite(gateway);
    assert.equal(result.success, false);
    assert.equal(result.phase, 'execution');
    assert.equal(result.completed_steps, 0);
    assert.equal(setup.calls.some(call => call.tool === 'type_text'), false);
});

const twoMainWindows = [
    { ProcessId: 10, WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z', Title: 'Editor' },
    { ProcessId: 10, WindowHandle: 101, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z', Title: 'Editor' }
];

function namedWriteLearning(evidence, ref) {
    return { evidence, name: 'editor.named_write', topic: 'Use/Editor', parameters: ['text'],
        outputs: [{ name: 'content', ref, field: 'value' }] };
}

test('a named demonstration window teaches and reuses its own window among two main windows', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-named-window-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory, twoMainWindows);
    const gateway = setup.gateway();
    await gateway.call('ui_info', {});
    const ref = (await gateway.call('ui_observe', { pid: 10, window: 101 })).elements.find(element => element.role === 'Edit').ref;
    const demonstration = await gateway.call('ui_act', { pid: 10, window: 101, steps: [{ action: 'write', ref, text: 'named' }] });
    assert.equal(demonstration.success, true);
    const learned = await gateway.call('cartridge_call', { function: 'Programming.learn',
        arguments: namedWriteLearning(demonstration.evidence, ref) });
    assert.equal(learned.success, true);
    const stored = JSON.parse(fs.readFileSync(gateway.learned.file, 'utf8')).functions.find(entry => entry.name === 'editor.named_write');
    assert.equal(stored.steps[0].selector.window, 'main');
    const reused = setup.gateway();
    await reused.call('ui_info', {});
    const unnamed = await reused.call('cartridge_call', { function: 'editor.named_write', arguments: { pid: 10, parameters: { text: 'x' } } });
    assert.equal(unnamed.success, false);
    assert.equal(unnamed.error, ambiguousWindowRefusal);
    assert.equal(setup.calls.some(call => call.tool === 'type_text' && call.args.text === 'x'), false);
    const named = await reused.call('cartridge_call', { function: 'editor.named_write',
        arguments: { pid: 10, window: 101, parameters: { text: 'reused' } } });
    assert.equal(named.success, true);
    assert.equal(setup.calls.filter(call => call.tool === 'type_text').at(-1).args.text, 'reused');
});

test('learning refuses an unnamed two-window demonstration and a demonstration window that vanished', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-vanished-window-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory, twoMainWindows);
    const gateway = setup.gateway();
    await gateway.call('ui_info', {});
    const ref = (await gateway.call('ui_observe', { pid: 10, window: 101 })).elements.find(element => element.role === 'Edit').ref;
    const demonstration = await gateway.call('ui_act', { pid: 10, window: 101, steps: [{ action: 'write', ref, text: 'named' }] });
    const learn = () => gateway.call('cartridge_call', { function: 'Programming.learn',
        arguments: namedWriteLearning(demonstration.evidence, ref) });
    gateway.demonstration.windowHandle = null;
    await assert.rejects(learn(), { message: uniqueWindowRefusal });
    gateway.demonstration.windowHandle = 101;
    setup.setWindows([twoMainWindows[0]]);
    await assert.rejects(learn(), { message: demonstrationWindowRefusal });
    assert.equal(gateway.learned.functions.some(entry => entry.name === 'editor.named_write'), false);
});

test('a read capture names its window and refuses a reference that belongs to another window', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-read-window-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const setup = fixture(directory, twoMainWindows);
    setup.setValue('observed');
    const gateway = setup.gateway();
    await gateway.call('ui_info', {});
    const ref = (await gateway.call('ui_observe', { pid: 10, window: 100 })).elements.find(element => element.role === 'Edit').ref;
    const readArguments = { pid: 10, window: 101, name: 'editor.read_value', topic: 'Use/Editor', ref, field: 'value', output: 'content' };
    await assert.rejects(gateway.call('cartridge_call', { function: 'Programming.learn_read', arguments: readArguments }),
        { message: referenceWindowRefusal });
    const learned = await gateway.call('cartridge_call', { function: 'Programming.learn_read',
        arguments: { ...readArguments, window: 100 } });
    assert.equal(learned.success, true);
});

// A menu editor whose Save item sits in a closed File menu. Saving clears the unsaved marker, disables Save and fills the Status field.
function menuEditor(directory) {
    const state = { title: 'Untitled* - Editor', status: '', saved: false, failSecondTreeAfterSave: false, treesAfterSave: 0 };
    const configuration = { cartridgeRoot: directory, auto_capture: false,
        application: { key: 'fixture-editor', version: '1.0.0', identity_source: 'controlled-test-fixture' } };
    const client = { call: async (tool, args) => {
        if (tool === 'get_ui_tree' && state.saved && state.failSecondTreeAfterSave && ++state.treesAfterSave === 2) {
            return { result: { structuredContent: { success: false, message: 'Native tree unavailable.' } } };
        }
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: state.title, WindowHandle: 100, Kind: 'main',
            ProcessStartedAtUtc: '2026-10-08T00:00:00Z' }];
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = `[1] Window "${state.title}" (autoId: "", val: "", enabled: True, offscreen: False)\n`
                + '  [2] menu item "Edit" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '    [3] menu item "Select All" (autoId: "", val: "", enabled: True, offscreen: True)\n'
                + '    [4] menu item "Convert" (autoId: "", val: "", enabled: True, offscreen: True)\n'
                + '      [5] menu item "To Uppercase" (autoId: "", val: "", enabled: True, offscreen: True)\n'
                + '  [6] menu item "File" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '    [7] menu item "Save" (autoId: "", val: "", enabled: ' + (state.saved ? 'False' : 'True') + ', offscreen: True)\n'
                + '  [8] Edit "Status" (autoId: "status", val: ' + JSON.stringify(state.status) + ', enabled: True, offscreen: False)';
        }
        if (tool === 'click_element' && args.id === 7) {
            state.saved = true;
            state.status = 'saved';
            state.title = state.title.replace('*', '');
        }
        return { result: { structuredContent: result } };
    } };
    const gateway = () => {
        const instance = new DesktopUiGateway(client, () => {}, configuration);
        instance.serverInfo = { name: 'node', version: '0.2610.0609' };
        return instance;
    };
    return { state, gateway };
}

// Demonstrates Select All, To Uppercase and Save on the menu editor, then learns them with the Status field as the goal output.
async function learnMenuSave(editor) {
    const teaching = editor.gateway();
    await teaching.call('ui_info', {});
    const view = await teaching.call('ui_observe', { pid: 10, offscreen: true });
    const find = (name, role) => view.elements.find(element => element.name === name && element.role === role).ref;
    const demonstration = await teaching.call('ui_act', { pid: 10, steps: [{ action: 'click', ref: find('Select All', 'menu item') },
        { action: 'click', ref: find('To Uppercase', 'menu item') }, { action: 'click', ref: find('Save', 'menu item') }] });
    assert.equal(demonstration.success, true);
    const learned = await teaching.call('cartridge_call', { function: 'Programming.learn', arguments: { evidence: demonstration.evidence,
        name: 'select-upper-save', topic: 'Use/Editor', parameters: [], outputs: [{ name: 'shown', ref: find('Status', 'Edit'), field: 'value' }] } });
    assert.equal(learned.success, true);
}

test('a stored function names its steps and reports only the window change, closed menu items included', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-call-steps-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const editor = menuEditor(directory);
    await learnMenuSave(editor);

    const reset = () => Object.assign(editor.state, { title: 'Untitled* - Editor', status: '', saved: false });
    reset();
    const later = editor.gateway();
    await later.call('ui_info', {});
    const run = await later.call('cartridge_call', { function: 'select-upper-save', arguments: { pid: 10 } });
    assert.equal(run.verified, true);
    assert.deepEqual(run.steps, ['click Edit > Select All', 'click Edit > Convert > To Uppercase', 'click File > Save']);
    assert.equal(run.ui.kind, 'delta');
    assert.ok(run.ui.changed.some(row => row.name === 'Save' && row.disabled === true && row.offscreen === true));
    assert.ok(run.ui.changed.some(row => row.role === 'Window' && row.name === 'Untitled - Editor'));
    assert.ok(run.ui.changed.some(row => row.name === 'Status' && row.value === 'saved'));
    assert.equal(run.ui.changed.some(row => row.name === 'Select All' || row.name === 'To Uppercase'), false);

    reset();
    const summary = await later.call('cartridge_call', { function: 'select-upper-save', arguments: { pid: 10, result_mode: 'summary' } });
    assert.equal(summary.verified, true);
    assert.equal(summary.ui.kind, 'delta');
    assert.equal(summary.observation_required, undefined);
    assert.equal(summary.steps.length, 3);
});

test('a verified stored run keeps its outcome when its change cannot be read, and asks for an observation instead', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-call-unread-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));
    const editor = menuEditor(directory);
    await learnMenuSave(editor);
    Object.assign(editor.state, { title: 'Untitled* - Editor', status: '', saved: false, failSecondTreeAfterSave: true, treesAfterSave: 0 });
    const later = editor.gateway();
    await later.call('ui_info', {});
    const run = await later.call('cartridge_call', { function: 'select-upper-save', arguments: { pid: 10 } });
    assert.equal(run.success, true);
    assert.equal(run.verified, true);
    assert.equal(run.ui, undefined);
    assert.equal(run.observation_required, true);
    assert.equal(run.observation_reason, 'changes_unreadable');
    assert.equal(run.ui_error, 'Native tree unavailable.');
});
