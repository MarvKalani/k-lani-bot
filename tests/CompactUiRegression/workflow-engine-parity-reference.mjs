import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { WorkflowStore } from '../../modules/desktop-ui-mcp/workflow-store.mjs';
import { Workflows } from '../../modules/desktop-ui-mcp/workflows.mjs';

// These adapter states are explicit synthetic faults, not application outputs or an acceptance oracle.
export async function workflowEngineFixtures(corpus) {
    const results = [];
    for (const fixture of corpus.cases) results.push(await runFixture(fixture));
    return results;
}

async function runFixture(fixture) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-engine-reference-'));
    try {
        let store = new WorkflowStore(root, { node: 'fixture-node', principal: 'owner', connection: 'fixture-client' });
        const definition = fixtureDefinition(fixture.decision);
        const adapter = new SyntheticAdapter(fixture.mode);
        let engine = new Workflows(store, adapter, new Map([['fixture', definition]]));
        let reply;
        const steps = [];
        const parameters = structuredClone(fixture.parameters);
        if (fixture.repeat_text) parameters.text = parameters.text.repeat(fixture.repeat_text);
        for (const action of fixture.actions) {
            if (action === 'restart') {
                store = new WorkflowStore(root, { node: 'fixture-node', principal: 'owner', connection: 'fixture-client' });
                engine = new Workflows(store, adapter, new Map([['fixture', definition]]));
                continue;
            }
            if (action.startsWith('inject-')) {
                const job = store.read(reply.job_ref);
                if (action === 'inject-prepared') {
                    job.pending = { stage_id: 'open', phase: 'prepared', before: evidence(1), parameters: job.parameters, operations: [] };
                    adapter.mode = 'success';
                } else {
                    job.pending.phase = 'verified';
                    job.pending.outputs = { value: adapter.value };
                }
                store.write(job);
                reply = engine.status(job.job_ref);
                continue;
            }
            try {
                reply = action === 'run' ? await engine.run({ workflow_key: 'fixture', revision: 1,
                    client_request_id: 'request', parameters }) : action === 'status' ? engine.status(reply.job_ref)
                    : await engine.resume({ job_ref: reply.job_ref, expected_revision: reply.revision - (fixture.stale ? 1 : 0),
                        answers: fixture.answers ?? {} });
                const job = store.read(reply.job_ref);
                steps.push(snapshot(reply, job, adapter));
            } catch (error) {
                steps.push({ error: error.message, dispatches: adapter.dispatches, queries: adapter.queries });
            }
        }
        return { name: fixture.name, steps };
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}

function fixtureDefinition(decision) {
    const stage = id => ({ id, function_ref: { application: 'editor', name: id }, bindings: { text: { input: 'text' } },
        preconditions: 'fresh_unique_enabled_targets', postconditions: 'recorded_function_outputs' });
    const open = stage('open');
    if (decision) open.decision = { input: 'approve', prompt: 'Approve the synthetic stage.' };
    return { format: 'k-lani-bot-workflow/1', key: 'fixture', revision: 1,
        inputs: { text: { type: 'string' }, ...(decision ? { approve: { type: 'boolean' } } : {}) },
        applications: { editor: { key: 'fixture-editor', version: '1.0', window: 'main' } },
        stages: [open, stage('write')], outputs: { result: { stage: 'write', output: 'value' } } };
}

function evidence(steps) {
    return { expected_steps: steps, mutation_steps: Array.from({ length: steps }, (_, index) => index) };
}

class SyntheticAdapter {
    constructor(mode) {
        this.mode = mode;
        this.value = 'old';
        this.dialog = false;
        this.dispatches = 0;
        this.queries = 0;
        this.ledger = new Map();
    }
    function() {
        return { validation: 'demonstrated_once', compatibility: {}, outputs: [{ name: 'value' }],
            inputSchema: { properties: { parameters: { properties: { text: { type: 'string' } }, required: ['text'] } } } };
    }
    async refreshInfo(job) {
        if (this.mode === 'changed-instance' && job.pending) throw new Error('Application instance changed.');
    }
    async prepare(stage) {
        if (stage.id === 'write' && (!this.dialog || this.mode === 'missing-target')) throw new Error('Later target absent.');
        if (this.mode === 'prepared') throw new Error('Target absent before dispatch.');
        return { evidence: evidence(['prefix', 'mixed-denied'].includes(this.mode) && stage.id === 'write' ? 2 : 1) };
    }
    async execute(stage, parameters, prepared, recorder) {
        const count = this.mode === 'mixed-denied' && stage.id === 'write' ? 2 : 1;
        for (let index = 0; index < count; index++) {
            recorder.step(index);
            const request = recorder.before('type_text', { text: parameters.text });
            const denied = stage.id === 'write' && (this.mode === 'denied' || this.mode === 'mixed-denied' && index === 1);
            const response = denied ? { success: false, outcome: 'not_dispatched', code: 'denied' }
                : { success: true, outcome: 'completed' };
            this.ledger.set(request.request_id, response);
            if (denied) {
                recorder.after(request.request_id, response);
                throw new Error('Native scope refused input.');
            }
            this.dispatches++;
            if (stage.id === 'open') this.dialog = true;
            else this.value = this.mode === 'append' ? 'old' + parameters.text : this.mode === 'empty' ? ''
                : this.mode === 'wrong-tail' ? parameters.text.slice(0, -1) + 'y' : parameters.text;
            if (stage.id === 'write' && ['lost', 'unknown', 'prefix', 'changed-instance', 'changed-function', 'changed-value'].includes(this.mode)) {
                throw new Error('Lost native response.');
            }
            recorder.after(request.request_id, response);
        }
        return { verified: true, outputs: { value: stage.id === 'open' ? 'opened' : this.value } };
    }
    async verify(stage, parameters, before, outputs) {
        return stage.id === 'open' ? this.dialog && outputs.value === 'opened'
            : this.value === parameters.text && outputs.value === this.value;
    }
    async operationStatus(id) {
        this.queries++;
        if (this.mode === 'unknown') return { success: false, outcome: 'unknown' };
        if (!this.ledger.has(id)) throw new Error('Unexpected original operation ID.');
        return this.ledger.get(id);
    }
    async reconcile(stage) {
        if (this.mode === 'changed-function') throw new Error('Function scope changed.');
        if (this.mode === 'changed-value') this.value = 'different';
        return { value: stage.id === 'open' ? 'opened' : this.value };
    }
}

function snapshot(reply, job, adapter) {
    const { job_ref, ...progress } = reply;
    return { progress, dispatches: adapter.dispatches, queries: adapter.queries, fingerprint: job.request_fingerprint,
        outputs: job.outputs, output_digests: job.output_digests ?? {}, final_outputs: job.final_outputs ?? null,
        pending: job.pending ? { phase: job.pending.phase, operations: job.pending.operations.map(operation => ({
            step_index: operation.step_index, phase: operation.phase, response: operation.response ?? null })) } : null };
}
