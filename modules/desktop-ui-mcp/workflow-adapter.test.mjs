import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DesktopUiGateway, unnamedWriteAllowed } from './gateway.mjs';

function setup(context) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-workflow-gateway-'));
    context.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const cartridgeRoot = path.join(root, 'cartridges');
    const stateRoot = path.join(root, 'jobs');
    let dialog = false;
    let value = '';
    let started = '2026-10-08T00:00:00Z';
    let changedSource = false;
    let loseResponse = false;
    let badReplacement = null;
    const mutations = [];
    const ledger = new Map();
    const client = { call: async (tool, args) => {
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi', nodeId: 'test-node' };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, WindowHandle: 100, Kind: 'main', Title: 'Editor', ProcessStartedAtUtc: started }];
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = '[1] Window "Editor" (autoId: "", val: "", enabled: True, offscreen: False)\n'
                + '  [2] Button "Open" (autoId: "open", val: "", enabled: True, offscreen: False)';
            if (dialog) result.tree += '\n  [3] Edit "Dialog text" (autoId: "text", val: ' + JSON.stringify(value) + ', enabled: True, offscreen: False)';
            result.tree += '\n  [4] Text "Source" (autoId: "source", val: "'
                + (changedSource ? 'changed source' : 'original source') + '", enabled: True, offscreen: False)';
        }
        if (tool === 'click_element' || tool === 'type_text') {
            mutations.push({ tool, args });
            assert.match(args.request_id, /^[a-f0-9]{8}-[a-f0-9]{4}-7/);
            const jobFile = fs.readdirSync(stateRoot).find(file => file.endsWith('.json'));
            assert.ok(fs.readFileSync(path.join(stateRoot, jobFile), 'utf8').includes(args.request_id));
            if (tool === 'click_element') dialog = true;
            else value = badReplacement === 'append' ? 'old' + value + args.text : badReplacement === 'empty' ? ''
                : badReplacement === 'wrong-tail' ? args.text.slice(0, -1) + 'y' : args.text;
            result.outcome = 'completed';
            ledger.set(args.request_id, result);
            if (loseResponse && tool === 'type_text') throw new Error('Response lost after native effect.');
        }
        if (tool === 'get_action_status') Object.assign(result, ledger.get(args.request_id) ?? { success: false, outcome: 'unknown' });
        return { result: { structuredContent: result } };
    } };
    const application = { key: 'fixture', version: '1.0', identity_source: 'controlled-test-fixture' };
    const selector = (role, name, autoId) => ({ role, name, autoId, window: 'main', containerPath: [] });
    const compatibility = { node_version: '0.2610.0609', platform: 'linux-x11-atspi', application_version: '1.0' };
    const schema = (names = []) => ({ type: 'object', properties: { pid: { type: 'integer', minimum: 1 },
        parameters: { type: 'object', properties: Object.fromEntries(names.map(name => [name, { type: 'string', maxLength: 16000 }])),
            required: names, additionalProperties: false } }, required: ['pid', 'parameters'], additionalProperties: false });
    const functions = [
        { name: 'read_source', topic: 'Use/Editor', steps: [{ action: 'read', selector: selector('Text', 'Source', 'source'), route: 'native-read' }],
            outputs: [{ name: 'value', selector: selector('Text', 'Source', 'source'), field: 'value', check: { kind: 'not_empty' } }],
            compatibility, inputSchema: schema(), validation: 'demonstrated_once; every execution verifies outputs' },
        { name: 'open', topic: 'Use/Editor', steps: [{ action: 'click', selector: selector('Button', 'Open', 'open'), route: 'semantic' }],
            outputs: [{ name: 'shown', selector: selector('Edit', 'Dialog text', 'text'), field: 'name', check: { kind: 'exists' } }],
            compatibility, inputSchema: schema(), validation: 'demonstrated_once; every execution verifies outputs' },
        { name: 'write', topic: 'Use/Editor', steps: [{ action: 'write', selector: selector('Edit', 'Dialog text', 'text'), route: 'native-text', parameter: 'text' }],
            outputs: [{ name: 'value', selector: selector('Edit', 'Dialog text', 'text'), field: 'value', check: { kind: 'equals_parameter', parameter: 'text' } }],
            compatibility, inputSchema: schema(['text']), validation: 'demonstrated_once; every execution verifies outputs' }
    ];
    const file = path.join(cartridgeRoot, 'programs/fixture/1.0/generated-ui.json');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify({ format: 'k-lani-bot-generated-ui-functions', version: 1, application, functions, candidates: [] }));
    const workflow = { stateRoot, cartridgeRoot, authority: { node: 'test-node', principal: 'owner', connection: 'authorized-client' },
        applications: [{ cartridgeRoot, application }] };
    const gateway = new DesktopUiGateway(client, () => {}, null, { workflow });
    gateway.serverInfo = { version: '0.2610.0609' };
    const definition = { format: 'k-lani-bot-workflow/1', key: 'dialog-flow', revision: 1, inputs: { text: { type: 'string' } },
        applications: { editor: { key: 'fixture', version: '1.0', window: 'main' } },
        stages: ['open', 'write'].map(id => ({ id, function_ref: { application: 'editor', name: id },
            bindings: id === 'write' ? { text: { input: 'text' } } : {}, preconditions: 'fresh_unique_enabled_targets', postconditions: 'recorded_function_outputs' })),
        outputs: { result: { stage: 'write', output: 'value' } } };
    return { gateway, definition, mutations, workflow, client, changeSource: () => { changedSource = true; },
        loseResponse: () => { loseResponse = true; },
        badReplacement: mode => { badReplacement = mode; },
        existsOutput: () => {
            const stored = JSON.parse(fs.readFileSync(file, 'utf8'));
            stored.functions.find(entry => entry.name === 'write').outputs[0].check = { kind: 'exists' };
            fs.writeFileSync(file, JSON.stringify(stored));
        },
        replaceTarget: () => { started = '2026-10-08T01:00:00Z'; } };
}

test('actual gateway stage preflight allows later appearing targets and checkpoints native UUIDv7 commands before dispatch', async context => {
    const testCase = setup(context);
    const call = (name, args) => testCase.gateway.call('cartridge_call', { function: name, arguments: args });
    assert.equal((await call('Programming.workflow_capture', { definition: testCase.definition })).stages, 2);
    const run = { workflow_key: 'dialog-flow', revision: 1, parameters: { text: 'PRIVATE_NEW_ADDRESS' }, client_request_id: 'one-request' };
    const result = await call('workflow.run', run);
    assert.equal(result.status, 'completed');
    assert.ok(result.evidence_ref);
    assert.equal(testCase.mutations.length, 2);
    assert.equal((await call('workflow.run', run)).job_ref, result.job_ref);
    assert.equal(testCase.mutations.length, 2);
    const document = fs.readFileSync(path.join(testCase.workflow.cartridgeRoot, 'programs/fixture/1.0/workflows.json'), 'utf8');
    assert.equal(document.includes('PRIVATE_NEW_ADDRESS'), false);
    assert.equal(document.includes('"pid"'), false);
    const restarted = new DesktopUiGateway(testCase.client, () => {}, null, { workflow: testCase.workflow });
    restarted.serverInfo = testCase.gateway.serverInfo;
    const status = await restarted.call('cartridge_call', { function: 'workflow.status', arguments: { job_ref: result.job_ref } });
    assert.equal(status.status, 'completed');
});

test('replaced application cannot resume a checkpointed target and emits no additional native mutation', async context => {
    const testCase = setup(context);
    const call = (name, args) => testCase.gateway.call('cartridge_call', { function: name, arguments: args });
    await call('Programming.workflow_capture', { definition: testCase.definition });
    const paused = await call('workflow.run', { workflow_key: 'dialog-flow', revision: 1, parameters: {}, client_request_id: 'request' });
    assert.equal(paused.status, 'awaiting_input');
    testCase.replaceTarget();
    const refused = await call('workflow.resume', { job_ref: paused.job_ref, expected_revision: paused.revision, answers: { text: 'never' } });
    assert.equal(refused.status, 'blocked');
    assert.match(refused.error, /replaced/);
    assert.equal(testCase.mutations.length, 1);
});

test('changed confirmed source digest refuses target input after an explicit pause', async context => {
    const testCase = setup(context);
    const definition = testCase.definition;
    definition.inputs = { confirm: { type: 'boolean' } };
    const read = structuredClone(definition.stages[0]);
    read.id = 'source';
    read.function_ref.name = 'read_source';
    definition.stages.splice(1, 0, read);
    definition.stages[2].bindings.text = { stage: 'source', output: 'value' };
    definition.stages[2].decision = { input: 'confirm', prompt: 'Transfer confirmed text?' };
    const call = (name, args) => testCase.gateway.call('cartridge_call', { function: name, arguments: args });
    await call('Programming.workflow_capture', { definition });
    const paused = await call('workflow.run', { workflow_key: definition.key, revision: 1, parameters: {}, client_request_id: 'source-request' });
    assert.equal(paused.status, 'awaiting_input');
    const before = testCase.mutations.length;
    testCase.changeSource();
    const refused = await call('workflow.resume', { job_ref: paused.job_ref, expected_revision: paused.revision, answers: { confirm: true } });
    assert.match(refused.error, /source digest changed/);
    assert.equal(refused.status, 'blocked');
    assert.equal(testCase.mutations.length, before);
});

test('workflow capture ignores unverified candidates and exposes only the selected schemas', async context => {
    const testCase = setup(context);
    const runtime = testCase.gateway.workflowRuntime;
    runtime.recordCaptured({ name: 'candidate' }, { key: 'fixture', version: '1.0' });
    assert.equal(runtime.captureSession, null);
    await runtime.call('Programming.workflow_begin', { key: 'captured', revision: 1,
        inputs: { text: { type: 'string' } }, applications: testCase.definition.applications });
    assert.throws(() => runtime.recordCaptured({ name: 'candidate' }, { key: 'fixture', version: '1.0' }), /Unverified/);
    assert.equal(runtime.captureSession.stages.length, 0);
    runtime.recordCaptured(runtime.adapter.function({ name: 'write' }, { key: 'fixture', version: '1.0' }), { key: 'fixture', version: '1.0' });
    const result = await runtime.call('Programming.workflow_finish', { outputs: { result: { stage: 'stage.1', output: 'value' } } });
    assert.equal(result.stages, 1);
    const listing = runtime.browse({ topic: 'Use/Workflows' });
    assert.equal(listing.functions.length, 3);
    assert.equal(listing.functions.some(item => item.inputSchema), false);
    assert.ok(runtime.browse({ function: 'workflow.resume' }).function.inputSchema);
});

test('an unnamed write needs a plain text entry bound to a window and a container path, for any application', () => {
    const bound = { role: 'Edit', windowKind: 'main', containerPath: [] };
    assert.equal(unnamedWriteAllowed(bound, 'write'), true);
    assert.equal(unnamedWriteAllowed({ role: 'Edit', window: 'main', containerPath: [] }, 'write'), true, 'a stored selector is bound too');
    assert.equal(unnamedWriteAllowed({ ...bound, role: 'TextArea' }, 'write'), true);
    assert.equal(unnamedWriteAllowed({ ...bound, role: 'entry' }, 'write'), true);
    assert.equal(unnamedWriteAllowed({ ...bound, windowKind: undefined, windowHandle: 100 }, 'write'), true);
    assert.equal(unnamedWriteAllowed({ ...bound, role: 'Button' }, 'write'), false);
    assert.equal(unnamedWriteAllowed({ ...bound, role: 'password text' }, 'write'), false);
    assert.equal(unnamedWriteAllowed({ ...bound, role: 'SecureTextField' }, 'write'), false);
    assert.equal(unnamedWriteAllowed({ ...bound, role: 'Document' }, 'write'), false);
    assert.equal(unnamedWriteAllowed(bound, 'click'), false);
    assert.equal(unnamedWriteAllowed({ ...bound, containerPath: undefined }, 'write'), false);
    assert.equal(unnamedWriteAllowed({ ...bound, windowKind: undefined }, 'write'), false);
});

test('public capture API records a freshly read source and an explicit typed decision without source contents', async context => {
    const testCase = setup(context);
    const gateway = new DesktopUiGateway(testCase.client, () => {}, testCase.workflow.applications[0], { workflow: testCase.workflow });
    gateway.serverInfo = testCase.gateway.serverInfo;
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const source = view.elements.find(element => element.name === 'Source');
    const call = (name, args) => gateway.call('cartridge_call', { function: name, arguments: args });
    await call('Programming.workflow_begin', { key: 'observed-read', revision: 1,
        inputs: { confirm: { type: 'boolean' } }, applications: testCase.definition.applications });
    await assert.rejects(call('Programming.learn_read', { pid: 10, topic: 'Info/Source', ref: source.ref,
        field: 'value', output: 'text' }), /Invalid observed read/);
    await call('Programming.learn_read', { pid: 10, name: 'observed_source', topic: 'Info/Source', ref: source.ref,
        field: 'value', output: 'text' });
    await assert.rejects(call('Programming.workflow_finish', { decisions: { unknown: { input: 'confirm', prompt: 'Read?' } },
        outputs: { text: { stage: 'stage.1', output: 'text' } } }), /captured stages/);
    await call('Programming.workflow_finish', { decisions: { 'stage.1': { input: 'confirm', prompt: 'Read?' } },
        outputs: { text: { stage: 'stage.1', output: 'text' } } });
    const text = fs.readFileSync(path.join(testCase.workflow.cartridgeRoot, 'programs/fixture/1.0/workflows.json'), 'utf8');
    assert.equal(text.includes('original source'), false);
    assert.equal(testCase.mutations.length, 0);
    const paused = await call('workflow.run', { workflow_key: 'observed-read', revision: 1, parameters: {}, client_request_id: 'read' });
    assert.equal(paused.status, 'awaiting_input');
    const completed = await call('workflow.resume', { job_ref: paused.job_ref, expected_revision: paused.revision, answers: { confirm: true } });
    assert.equal(completed.status, 'completed');
    assert.equal(testCase.mutations.length, 0);
});

test('accepted metadata-only revision does not replay a pending workflow effect; changed recipe body still invalidates it', async context => {
    const testCase = setup(context);
    const call = (name, args) => testCase.gateway.call('cartridge_call', { function: name, arguments: args });
    await call('Programming.workflow_capture', { definition: testCase.definition });
    testCase.loseResponse();
    const unknown = await call('workflow.run', { workflow_key: 'dialog-flow', revision: 1,
        parameters: { text: 'confirmed pending effect' }, client_request_id: 'pending' });
    assert.equal(unknown.status, 'unconfirmed');
    assert.equal(testCase.mutations.length, 2);
    const child = testCase.gateway.workflowRuntime.adapter.gateway({ key: 'fixture', version: '1.0' });
    const functions = structuredClone(child.learned.functions);
    for (const entry of functions) entry.compatibility.node_version = 'metadata-patch';
    child.learned.save(functions, []);
    testCase.gateway.serverInfo.version = 'metadata-patch';
    const completed = await call('workflow.resume', { job_ref: unknown.job_ref, expected_revision: unknown.revision });
    assert.equal(completed.status, 'completed');
    assert.equal(testCase.mutations.length, 2);
    const changed = setup(context);
    const changedCall = (name, args) => changed.gateway.call('cartridge_call', { function: name, arguments: args });
    await changedCall('Programming.workflow_capture', { definition: changed.definition });
    changed.loseResponse();
    const interrupted = await changedCall('workflow.run', { workflow_key: 'dialog-flow', revision: 1,
        parameters: { text: 'pending effect' }, client_request_id: 'changed-body' });
    const changedChild = changed.gateway.workflowRuntime.adapter.gateway({ key: 'fixture', version: '1.0' });
    const edited = structuredClone(changedChild.learned.functions);
    edited.find(entry => entry.name === 'write').steps[0].selector.name = 'Changed caption';
    changedChild.learned.save(edited, []);
    const rejected = await changedCall('workflow.resume', { job_ref: interrupted.job_ref, expected_revision: interrupted.revision });
    assert.equal(rejected.status, 'unconfirmed');
    assert.match(rejected.error, /function changed/);
    assert.equal(changed.mutations.length, 2);
});


for (const mode of ['append', 'empty', 'wrong-tail']) {
    test('resume rejects ' + mode + ' field replacement even with an Exists output and successful original ledger', async context => {
        const testCase = setup(context);
        const call = (name, args) => testCase.gateway.call('cartridge_call', { function: name, arguments: args });
        testCase.existsOutput();
        testCase.badReplacement(mode);
        testCase.loseResponse();
        await call('Programming.workflow_capture', { definition: testCase.definition });
        const expected = mode === 'wrong-tail' ? 'x'.repeat(16000) : 'requested';
        const interrupted = await call('workflow.run', { workflow_key: 'dialog-flow', revision: 1,
            parameters: { text: expected }, client_request_id: 'bad-value' });
        assert.equal(interrupted.status, 'unconfirmed');
        const resumed = await call('workflow.resume', { job_ref: interrupted.job_ref, expected_revision: interrupted.revision });
        assert.equal(resumed.status, 'unconfirmed');
        assert.match(resumed.error, /written input/);
        assert.equal(testCase.mutations.length, 2);
    });
}
