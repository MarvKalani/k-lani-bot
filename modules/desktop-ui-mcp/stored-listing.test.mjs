import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DesktopUiGateway, ambiguousWindowRefusal, pidRefusal, usageHint, stateHint } from './gateway.mjs';
import { selectProcessHint, summarizeListedFunctions, descriptionRefusal } from './learned-functions.mjs';

const mousepadWindow = { ProcessId: 10, WindowHandle: 100, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z',
    Title: 'Document', ProcessName: 'mousepad', AppVersion: '3.0' };
const geditWindow = { ProcessId: 20, WindowHandle: 200, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z',
    Title: 'Notes', ProcessName: 'gedit', AppVersion: '46.0' };
const secondMousepadWindow = { ...mousepadWindow, WindowHandle: 101, Title: 'Second document' };

// A native-version program: Mousepad 3.0 is the configured cartridge, gedit is an application without stored functions.
function fixture(directory, windows = [mousepadWindow, geditWindow]) {
    let listed = windows;
    let value = '';
    const calls = [];
    const configuration = { cartridgeRoot: directory, auto_capture: false,
        application: { key: 'mousepad', version: '3.0', identity_source: 'native-version', process_name: 'mousepad' } };
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = listed;
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = '[1] Window "Document" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [2] Edit "Document" (autoId: "document", val: ' + JSON.stringify(value) + ', enabled: True, offscreen: False)';
        }
        if (tool === 'type_text') value = args.text;
        return { result: { structuredContent: result } };
    } };
    return { calls, gateway: () => {
        const instance = new DesktopUiGateway(client, () => {}, configuration);
        instance.serverInfo = { name: 'node', version: '0.2610.0609' };
        return instance;
    } };
}

// Demonstrates one write in the given window and turns it into a stored Use/Editor function, as a person would.
async function teach(gateway, window = undefined, description = undefined) {
    await gateway.call('ui_info', {});
    const handle = window === undefined ? {} : { window };
    const view = await gateway.call('ui_observe', { pid: 10, ...handle });
    const ref = view.elements.find(element => element.role === 'Edit').ref;
    const demonstration = await gateway.call('ui_act', { pid: 10, ...handle, steps: [{ action: 'write', ref, text: 'TRAINING' }] });
    return gateway.call('cartridge_call', { function: 'Programming.learn', arguments: { evidence: demonstration.evidence,
        name: 'replace-text-and-save', topic: 'Use/Editor', parameters: ['text'], outputs: [{ name: 'content', ref, field: 'value' }],
        ...(description === undefined ? {} : { description }) } });
}

function temporaryDirectory(context) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-stored-listing-'));
    context.after(() => fs.rmSync(directory, { recursive: true, force: true }));

    return directory;
}

test('ui_info names the stored functions of the row that runs them, and no other row, with a usage hint', async context => {
    const setup = fixture(temporaryDirectory(context));
    assert.equal((await teach(setup.gateway())).success, true);
    const info = await setup.gateway().call('ui_info', {});
    const mousepad = info.windows.find(window => window.pid === 10);
    const gedit = info.windows.find(window => window.pid === 20);
    assert.deepEqual(mousepad.functions, [{ name: 'replace-text-and-save', parameters: ['text'], steps: ['write Document'] }]);
    assert.equal(mousepad.more_functions, undefined);
    assert.equal('functions' in gedit, false);
    assert.equal(info.next, usageHint + ' ' + stateHint);
});

test('ui_info has no functions field and only the state hint when nothing is stored, or when the row identity is unknown', async context => {
    const directory = temporaryDirectory(context);
    const setup = fixture(directory);
    const empty = await setup.gateway().call('ui_info', {});
    assert.equal(empty.next, stateHint);
    assert.equal(empty.windows.some(window => 'functions' in window), false);
    await teach(setup.gateway());
    const unidentified = await fixture(directory, [{ ...mousepadWindow, ProcessName: undefined, AppVersion: undefined }])
        .gateway().call('ui_info', {});
    assert.equal(unidentified.next, stateHint);
    assert.equal('functions' in unidentified.windows[0], false);
});

test('ui_info lists at most twelve stored names in code-unit order and counts the rest', async context => {
    const setup = fixture(temporaryDirectory(context));
    const first = setup.gateway();
    await teach(first);
    const original = first.learned.get('replace-text-and-save');
    for (let index = 0; index < 12; index++) {
        first.learned.install({ ...structuredClone(original), name: 'listed-' + String(index).padStart(2, '0') });
    }
    const info = await setup.gateway().call('ui_info', {});
    const mousepad = info.windows.find(window => window.pid === 10);
    assert.deepEqual(mousepad.functions, Array.from({ length: 12 }, (_, index) => ({ name: 'listed-' + String(index).padStart(2, '0'),
        parameters: ['text'], steps: ['write Document'] })));
    assert.equal(mousepad.more_functions, 1);
});

test('ui_info lists named functions only, while a generated name stays browsable and resolves without a selected process', async context => {
    const setup = fixture(temporaryDirectory(context));
    const first = setup.gateway();
    await teach(first);
    first.learned.install({ ...structuredClone(first.learned.get('replace-text-and-save')), name: 'ui.element.write.c1b6dc7e' });
    const listing = setup.gateway();
    const info = await listing.call('ui_info', {});
    assert.deepEqual(info.windows.find(window => window.pid === 10).functions,
        [{ name: 'replace-text-and-save', parameters: ['text'], steps: ['write Document'] }]);
    const generated = await listing.call('cartridge_browse', { function: 'ui.element.write.c1b6dc7e' });
    assert.equal(generated.function.name, 'ui.element.write.c1b6dc7e');
});

test('cartridge_info and a browse without a topic name where stored functions are listed, and a topic browse does not', async context => {
    const setup = fixture(temporaryDirectory(context));
    await teach(setup.gateway());
    const gateway = setup.gateway();
    assert.equal((await gateway.call('cartridge_browse', {})).stored_functions, selectProcessHint);
    const info = await gateway.call('cartridge_info', {});
    assert.equal(info.stored_functions, 'Listed per window in ui_info.');
    assert.equal((await gateway.call('cartridge_browse', {})).stored_functions, 'Listed per window in ui_info.');
    assert.equal((await gateway.call('cartridge_browse', { topic: 'Use' })).stored_functions, undefined);
});

test('after a ui_info without functions, cartridge_info and both browses say that nothing is stored', async context => {
    const gateway = fixture(temporaryDirectory(context)).gateway();
    await gateway.call('ui_info', {});
    const expected = 'No stored functions for the current windows; '
        + 'read a window with ui_observe {pid, as: "state"} and change it with ui_apply.';
    assert.equal((await gateway.call('cartridge_info', {})).stored_functions, expected);
    assert.equal((await gateway.call('cartridge_browse', {})).stored_functions, expected);
    const topical = await gateway.call('cartridge_browse', { topic: 'Use' });
    assert.equal(topical.functions.some(entry => !entry.name.startsWith('form.')), false);
    assert.equal(topical.hint, expected);
});

test('the summary sorts by name in code-unit order, keeps twelve, names parameters only when there are some, and counts the rest', () => {
    const functions = [{ name: 'zeta', parameters: ['text'], steps: [] }, { name: 'alpha', parameters: [], steps: ['click Close'] },
        ...Array.from({ length: 11 }, (_, index) => ({ name: 'item-' + index, parameters: ['text', 'encoding'], steps: [] }))];
    const summary = summarizeListedFunctions(functions);
    assert.equal(summary.functions.length, 12);
    assert.deepEqual(summary.functions.slice(0, 2),
        [{ name: 'alpha', steps: ['click Close'] }, { name: 'item-0', parameters: ['text', 'encoding'], steps: [] }]);
    assert.equal(summary.more, 1);
    assert.deepEqual(functions.slice(0, 2).map(entry => entry.name), ['zeta', 'alpha']);
    assert.deepEqual(summarizeListedFunctions([{ name: 'only', description: 'Writes the text.', parameters: ['text'], steps: ['write Text'] }]),
        { functions: [{ name: 'only', description: 'Writes the text.', parameters: ['text'], steps: ['write Text'] }], more: 0 });
});

test('a description given at learning survives a restart; ui_info and a browse by name show it with the step labels', async context => {
    const setup = fixture(temporaryDirectory(context));
    const description = 'Writes the given text into the document.';
    assert.equal((await teach(setup.gateway(), undefined, description)).success, true);
    const gateway = setup.gateway();
    const info = await gateway.call('ui_info', {});
    const mousepad = info.windows.find(window => window.pid === 10);
    assert.deepEqual(mousepad.functions,
        [{ name: 'replace-text-and-save', description, parameters: ['text'], steps: ['write Document'] }]);
    const browsed = await gateway.call('cartridge_browse', { function: 'replace-text-and-save' });
    assert.equal(browsed.function.description, description);
    assert.deepEqual(browsed.function.steps, ['write Document']);
});

test('a 201-character description refuses the learn with its own wording, and nothing is stored', async context => {
    const directory = temporaryDirectory(context);
    const setup = fixture(directory);
    await assert.rejects(teach(setup.gateway(), undefined, 'a'.repeat(201)), { message: descriptionRefusal });
    const info = await setup.gateway().call('ui_info', {});
    assert.equal('functions' in info.windows.find(window => window.pid === 10), false);
});

test('a stored function runs right after ui_info, because its pid selects the process without a prior observation', async context => {
    const setup = fixture(temporaryDirectory(context));
    await teach(setup.gateway());
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const reply = await restarted.call('cartridge_call', { function: 'replace-text-and-save',
        arguments: { pid: 10, parameters: { text: 'SECOND' } } });
    assert.equal(reply.verified, true);
    assert.deepEqual(reply.outputs, { content: 'SECOND' });
});

test('two main windows of one program refuse a stored call without a window, and the named window runs', async context => {
    const setup = fixture(temporaryDirectory(context), [mousepadWindow, secondMousepadWindow, geditWindow]);
    assert.equal((await teach(setup.gateway(), 100)).success, true);
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const refused = await restarted.call('cartridge_call', { function: 'replace-text-and-save',
        arguments: { pid: 10, parameters: { text: 'NO' } } });
    assert.equal(refused.success, false);
    assert.equal(refused.error, ambiguousWindowRefusal);
    const named = await restarted.call('cartridge_call', { function: 'replace-text-and-save',
        arguments: { pid: 10, window: 101, parameters: { text: 'NAMED' } } });
    assert.equal(named.verified, true);
});

test('a PID that ui_info did not list is refused for a stored function, as for observation', async context => {
    const setup = fixture(temporaryDirectory(context));
    await teach(setup.gateway());
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    await assert.rejects(restarted.call('cartridge_call', { function: 'replace-text-and-save',
        arguments: { pid: 99, parameters: { text: 'never' } } }), { message: pidRefusal });
});

test('browse offers stored functions only through a selected process and names the selection when none exists', async context => {
    const setup = fixture(temporaryDirectory(context));
    await teach(setup.gateway());
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const unselected = await restarted.call('cartridge_browse', { topic: 'Use/Editor' });
    assert.deepEqual(unselected.functions, []);
    assert.equal(unselected.hint, selectProcessHint);
    const named = await restarted.call('cartridge_browse', { function: 'replace-text-and-save' });
    assert.equal(named.function.name, 'replace-text-and-save');
    assert.equal(restarted.selectedPid, null);
    const selected = await restarted.call('cartridge_browse', { topic: 'Use/Editor', window: 100 });
    assert.deepEqual(selected.functions.map(entry => entry.name), ['replace-text-and-save']);
    assert.equal(selected.hint, undefined);
    const loaded = await restarted.call('cartridge_browse', { function: 'replace-text-and-save' });
    assert.equal(loaded.function.name, 'replace-text-and-save');
});

test('a process with two main windows lists its stored functions for the window the caller names', async context => {
    const setup = fixture(temporaryDirectory(context), [mousepadWindow, secondMousepadWindow, geditWindow]);
    await teach(setup.gateway(), 100);
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const listed = await restarted.call('cartridge_browse', { topic: 'Use/Editor', window: 101 });
    assert.deepEqual(listed.functions.map(entry => entry.name), ['replace-text-and-save']);
    assert.equal(listed.hint, undefined);
});

test('an unknown window handle in browse is refused, as in ui_info', async context => {
    const setup = fixture(temporaryDirectory(context));
    await teach(setup.gateway());
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    await assert.rejects(restarted.call('cartridge_browse', { topic: 'Use', window: 999 }), /not one of the current windows/);
});

test('a successful form read selects its process, so a Use browse of the same program offers stored functions', async context => {
    const setup = fixture(temporaryDirectory(context));
    await teach(setup.gateway());
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const view = await restarted.call('ui_observe', { pid: 10 });
    const scope = view.elements.find(element => element.role === 'Window').ref;
    // The observation just selected the process; clear that so that only the form read can select it.
    restarted.selectedPid = null;
    assert.equal((await restarted.call('cartridge_browse', { topic: 'Use/Editor' })).hint, selectProcessHint);
    const form = await restarted.call('cartridge_call', { function: 'form.read', arguments: { pid: 10, scope } });
    assert.equal(form.success, true);
    assert.equal((await restarted.call('cartridge_browse', { topic: 'Use/Editor' })).hint, undefined);
});

test('a refused two-window call selects nothing, so a Use browse without a window still answers with the selection hint', async context => {
    const setup = fixture(temporaryDirectory(context), [mousepadWindow, secondMousepadWindow, geditWindow]);
    await teach(setup.gateway(), 100);
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const refused = await restarted.call('cartridge_call', { function: 'replace-text-and-save',
        arguments: { pid: 10, parameters: { text: 'NO' } } });
    assert.equal(refused.success, false);
    assert.equal(refused.error, ambiguousWindowRefusal);
    const browse = await restarted.call('cartridge_browse', { topic: 'Use' });
    assert.equal(browse.hint, selectProcessHint);
    assert.equal(browse.functions.some(entry => entry.name === 'replace-text-and-save'), false);
});

test('the selection hint names ui_info and its rows with this exact wording', async context => {
    const setup = fixture(temporaryDirectory(context), [mousepadWindow, geditWindow]);
    await teach(setup.gateway(), 100);
    const browse = await setup.gateway().call('cartridge_browse', { topic: 'Use' });
    assert.equal(browse.hint, 'Call ui_info first; each window row lists its stored functions with their parameters.');
});

test('a summary of a click-only action with learning configured reports the click and asks for no output', async context => {
    const gateway = fixture(temporaryDirectory(context), [mousepadWindow]).gateway();
    await gateway.call('ui_info', {});
    const ref = (await gateway.call('ui_observe', { pid: 10 })).elements.find(element => element.role === 'Edit').ref;
    const plain = await gateway.call('ui_act', { pid: 10, steps: [{ action: 'click', ref }] });
    const summary = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'click', ref }] });
    assert.equal(plain.success, true);
    assert.equal(summary.success, true, summary.error);
    assert.equal(summary.error, undefined);
});

test('a summary of a tab click with automatic capture on does not fail for a missing heading', async context => {
    const configuration = { cartridgeRoot: temporaryDirectory(context),
        application: { key: 'mousepad', version: '3.0', identity_source: 'native-version', process_name: 'mousepad' } };
    const client = { call: async (tool) => {
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = [mousepadWindow];
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = '[1] Window "Document" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [2] TabItem "Tab A" (autoId: "", val: "", enabled: True, offscreen: False)';
        }
        return { result: { structuredContent: result } };
    } };
    const gateway = new DesktopUiGateway(client, () => {}, configuration);
    gateway.serverInfo = { name: 'node', version: '0.2610.0609' };
    await gateway.call('ui_info', {});
    const ref = (await gateway.call('ui_observe', { pid: 10 })).elements.find(element => element.name === 'Tab A').ref;
    const summary = await gateway.call('ui_act', { pid: 10, result_mode: 'summary', steps: [{ action: 'click', ref }] });
    assert.equal(summary.success, true, summary.error);
});

test('a stored function that declares no parameters requires only its pid and runs when called with the pid alone', async context => {
    const gateway = fixture(temporaryDirectory(context), [mousepadWindow]).gateway();
    await gateway.call('ui_info', {});
    const ref = (await gateway.call('ui_observe', { pid: 10 })).elements.find(element => element.role === 'Edit').ref;
    await gateway.call('ui_act', { pid: 10, steps: [{ action: 'write', ref, text: 'Hello' }] });
    const learned = await gateway.call('cartridge_call', { function: 'Programming.learn_read',
        arguments: { pid: 10, name: 'read-field', topic: 'Use/Reads', ref, field: 'value', output: 'text' } });
    assert.equal(learned.success, true);
    const browsed = await gateway.call('cartridge_browse', { function: 'read-field', pid: 10 });
    assert.deepEqual(browsed.function.inputSchema.required, ['pid']);
    const called = await gateway.call('cartridge_call', { function: 'read-field', arguments: { pid: 10 } });
    assert.equal(called.success, true);
});

test('learning refuses a missing or unknown evidence token with the token wording, before any PID check', async context => {
    const gateway = fixture(temporaryDirectory(context), [mousepadWindow]).gateway();
    await gateway.call('ui_info', {});
    const learning = { name: 'typed-text', topic: 'Use/Test', parameters: [], outputs: [{ name: 'value', ref: 'ref-1', field: 'value' }] };
    const wording = 'The evidence token is missing or unknown; pass the evidence value from the latest successful ui_act reply.';
    await assert.rejects(gateway.call('cartridge_call', { function: 'Programming.learn', arguments: learning }), { message: wording });
    await assert.rejects(gateway.call('cartridge_call', { function: 'Programming.learn', arguments: { ...learning, evidence: 'stale' } }),
        { message: wording });
});

test('after ui_info a stored function named without a window resolves to its schema, selects nothing, and accepts a listed pid', async context => {
    const setup = fixture(temporaryDirectory(context), [mousepadWindow, geditWindow]);
    await teach(setup.gateway(), 100);
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const named = await restarted.call('cartridge_browse', { function: 'replace-text-and-save' });
    assert.equal(named.function.name, 'replace-text-and-save');
    assert.equal(restarted.selectedPid, null);
    const byPid = await restarted.call('cartridge_browse', { function: 'replace-text-and-save', pid: 10 });
    assert.equal(byPid.function.name, 'replace-text-and-save');
    assert.equal(restarted.selectedPid, 10);
});

test('two processes of one program are one program, so a bare stored-function name resolves and a pid names either process', async context => {
    const otherProcess = { ...mousepadWindow, ProcessId: 11, WindowHandle: 102, ProcessStartedAtUtc: '2026-10-08T00:01:00Z' };
    const setup = fixture(temporaryDirectory(context), [mousepadWindow, otherProcess]);
    await teach(setup.gateway(), 100);
    const restarted = setup.gateway();
    await restarted.call('ui_info', {});
    const named = await restarted.call('cartridge_browse', { function: 'replace-text-and-save' });
    assert.equal(named.function.name, 'replace-text-and-save');
    const byPid = await restarted.call('cartridge_browse', { function: 'replace-text-and-save', pid: 11 });
    assert.equal(byPid.function.name, 'replace-text-and-save');
});
