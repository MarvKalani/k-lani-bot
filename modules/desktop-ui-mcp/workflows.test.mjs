import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { WorkflowStore } from './workflow-store.mjs';
import { Workflows } from './workflows.mjs';

function setup(context) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-staged-test-'));
    context.after(() => fs.rmSync(root, { recursive: true, force: true }));
    let dialog = false;
    let value = '';
    let lost = false;
    let ledgerAvailable = true;
    let missing = false;
    let denied = false;
    let writes = 0;
    const ledger = new Map();
    const entry = { validation: 'demonstrated_once', compatibility: {}, outputs: [{ name: 'value' }],
        inputSchema: { properties: { parameters: { properties: { text: { type: 'string' } }, required: ['text'] } } } };
    const stage = id => ({ id, function_ref: { application: 'editor', name: id }, bindings: { text: { input: 'text' } },
        preconditions: 'fresh_unique_enabled_targets', postconditions: 'recorded_function_outputs' });
    const definition = { format: 'k-lani-bot-workflow/1', key: 'dialog-job', revision: 1, inputs: { text: { type: 'string' } },
        applications: { editor: { key: 'fixture', version: '1.0', window: 'main' } }, stages: [stage('open'), stage('write')],
        outputs: { result: { stage: 'write', output: 'value' } } };
    const adapter = {
        function: () => entry,
        refreshInfo: async () => {},
        prepare: async current => {
            if (current.id === 'write' && (!dialog || missing)) throw new Error('Stage target is absent.');
            return { evidence: { value, dialog } };
        },
        execute: async (current, parameters, prepared, recorder) => {
            const args = recorder.before('type_text', { text: parameters.text });
            const file = fs.readFileSync(store.filename(jobReference), 'utf8');
            assert.ok(file.includes(args.request_id), 'Operation intent must be durable before the native send');
            writes++;
            if (denied) {
                writes--;
                const response = { success: false, outcome: 'not_dispatched', code: 'denied' };
                recorder.after(args.request_id, response);
                throw new Error('Required scope denied.');
            }
            if (current.id === 'open') dialog = true;
            else value = parameters.text;
            const response = { success: true, outcome: 'completed' };
            ledger.set(args.request_id, response);
            if (lost && current.id === 'write') throw new Error('Response lost after actual mutation.');
            recorder.after(args.request_id, response);
            return { verified: true, outputs: { value: current.id === 'open' ? 'opened' : value } };
        },
        verify: async (current, parameters, before, outputs) => current.id === 'open' ? dialog : value === parameters.text && outputs.value === value,
        operationStatus: async identifier => {
            if (!ledgerAvailable) throw new Error('Native ledger unavailable.');
            return ledger.get(identifier) ?? { success: false, outcome: 'unknown' };
        },
        reconcile: async current => ({ value: current.id === 'open' ? 'opened' : value })
    };
    const store = new WorkflowStore(root, { node: 'node', principal: 'owner', connection: 'client' });
    const engine = new Workflows(store, adapter, new Map([[definition.key, definition]]));
    let jobReference;
    const run = async parameters => {
        const job = store.create('request', 'test', definition, parameters);
        jobReference = job.job_ref;
        return store.serialized(job.job_ref, current => engine.advance(current));
    };
    return { engine, store, run, definition, writes: () => writes, loseResponse: () => { lost = true; },
        missingTarget: () => { missing = true; }, denyScope: () => { denied = true; }, unavailableLedger: () => { ledgerAvailable = false; } };
}

test('dialog text target appears only after stage one; per-stage preflight permits the verified workflow', async context => {
    const testCase = setup(context);
    const result = await testCase.run({ text: 'PRIVATE_CHANGED_ADDRESS' });
    assert.equal(result.status, 'completed');
    assert.deepEqual(result.completed_stages, ['open', 'write']);
    assert.equal(testCase.writes(), 2);
    assert.equal(JSON.stringify(testCase.definition).includes('PRIVATE_CHANGED_ADDRESS'), false);
});

test('missing later target sends no input for that stage and missing preference is awaiting_input', async context => {
    const testCase = setup(context);
    testCase.missingTarget();
    const result = await testCase.run({ text: 'address' });
    assert.equal(result.status, 'blocked');
    assert.deepEqual(result.completed_stages, ['open']);
    assert.equal(testCase.writes(), 1);
    const another = setup(context);
    const awaiting = await another.run({});
    assert.equal(awaiting.status, 'awaiting_input');
    assert.equal(another.writes(), 0);
});

test('lost mutation response is reconciled through node ledger and actual state without another dispatch', async context => {
    const testCase = setup(context);
    testCase.loseResponse();
    const unknown = await testCase.run({ text: 'new address' });
    assert.equal(unknown.status, 'unconfirmed');
    assert.equal(testCase.writes(), 2);
    const completed = await testCase.engine.resume({ job_ref: unknown.job_ref, expected_revision: unknown.revision });
    assert.equal(completed.status, 'completed');
    assert.equal(testCase.writes(), 2);
    await assert.rejects(testCase.engine.resume({ job_ref: unknown.job_ref, expected_revision: unknown.revision }), /stale_job_revision/);
});

test('unavailable native ledger leaves an unknown effect unconfirmed and never invents a new retry ID', async context => {
    const testCase = setup(context);
    testCase.loseResponse();
    const result = await testCase.run({ text: 'new address' });
    const identifiers = testCase.store.read(result.job_ref).pending.operations.map(operation => operation.request_id);
    testCase.unavailableLedger();
    const resumed = await testCase.engine.resume({ job_ref: result.job_ref, expected_revision: result.revision });
    assert.equal(resumed.status, 'unconfirmed');
    assert.equal(testCase.writes(), 2);
    assert.deepEqual(testCase.store.read(result.job_ref).pending.operations.map(operation => operation.request_id), identifiers);
});

test('a crash before dispatch can continue; a verified checkpoint reconciles without replaying native input', async context => {
    const testCase = setup(context);
    const completed = await testCase.run({ text: 'changed address' });
    const job = testCase.store.read(completed.job_ref);
    job.status = 'unconfirmed';
    job.completed_stages = ['open'];
    job.pending = { stage_id: 'write', phase: 'prepared', before: { value: '' }, parameters: { text: 'changed address' }, operations: [] };
    testCase.store.write(job);
    const before = testCase.writes();
    const result = await testCase.engine.resume({ job_ref: job.job_ref, expected_revision: job.revision });
    assert.equal(result.status, 'completed');
    assert.equal(testCase.writes(), before + 1);
    const checkpoint = testCase.store.read(result.job_ref);
    checkpoint.status = 'unconfirmed';
    checkpoint.completed_stages = ['open'];
    checkpoint.pending = { stage_id: 'write', phase: 'verified', before: { value: '' }, parameters: { text: 'changed address' },
        operations: [], outputs: { value: 'changed address' } };
    testCase.store.write(checkpoint);
    const final = await testCase.engine.resume({ job_ref: checkpoint.job_ref, expected_revision: checkpoint.revision });
    assert.equal(final.status, 'completed');
    assert.equal(testCase.writes(), before + 1);
});

test('concurrent resume serializes revision checks and sends each remaining mutation once', async context => {
    const testCase = setup(context);
    const paused = await testCase.run({});
    const answers = { job_ref: paused.job_ref, expected_revision: paused.revision, answers: { text: 'confirmed' } };
    const results = await Promise.allSettled([testCase.engine.resume(answers), testCase.engine.resume(answers)]);
    assert.equal(results.filter(result => result.status === 'fulfilled').length, 1);
    assert.match(results.find(result => result.status === 'rejected').reason.message, /stale_job_revision/);
    assert.equal(testCase.writes(), 2);
});

test('an unconsumed declined decision can be confirmed; previously used inputs stay immutable', async context => {
    const testCase = setup(context);
    testCase.definition.inputs.confirm = { type: 'boolean' };
    testCase.definition.stages[1].decision = { input: 'confirm', prompt: 'Confirm the remaining write.' };
    const paused = await testCase.run({ text: 'approved text', confirm: false });
    assert.equal(paused.status, 'awaiting_input');
    assert.equal(testCase.writes(), 1);
    await assert.rejects(testCase.engine.resume({ job_ref: paused.job_ref, expected_revision: paused.revision,
        answers: { text: 'replacement' } }), /cannot be replaced/);
    const result = await testCase.engine.resume({ job_ref: paused.job_ref, expected_revision: paused.revision, answers: { confirm: true } });
    assert.equal(result.status, 'completed');
    assert.equal(testCase.writes(), 2);
});

test('a crash after only a stage prefix cannot confirm or replay the unattempted remainder', async context => {
    const testCase = setup(context);
    testCase.loseResponse();
    const unknown = await testCase.run({ text: 'actual prefix value' });
    const job = testCase.store.read(unknown.job_ref);
    job.pending.before.expected_steps = 2;
    job.pending.step_index = 0;
    testCase.store.write(job);
    const result = await testCase.engine.resume({ job_ref: job.job_ref, expected_revision: job.revision });
    assert.equal(result.status, 'unconfirmed');
    assert.match(result.error, /prefix/);
    assert.equal(testCase.writes(), 2);
    const lastStarted = testCase.store.read(job.job_ref);
    lastStarted.pending.step_index = 1;
    lastStarted.pending.before.mutation_steps = [0, 1];
    lastStarted.pending.operations[0].step_index = 0;
    testCase.store.write(lastStarted);
    const final = await testCase.engine.resume({ job_ref: lastStarted.job_ref, expected_revision: lastStarted.revision });
    assert.equal(final.status, 'unconfirmed');
    assert.match(final.error, /prefix/);
    assert.equal(testCase.writes(), 2);
});

test('an explicit node scope refusal before any effect is blocked and retains the refused operation', async context => {
    const testCase = setup(context);
    testCase.denyScope();
    const result = await testCase.run({ text: 'not sent' });
    assert.equal(result.status, 'blocked');
    assert.equal(testCase.writes(), 0);
    const operation = testCase.store.read(result.job_ref).pending.operations[0];
    assert.equal(operation.response.outcome, 'not_dispatched');
    assert.equal(operation.response.code, 'denied');
});
