import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { WorkflowStore } from '../../modules/desktop-ui-mcp/workflow-store.mjs';

// Synthetic runtime data uses real private JS checkpoints. Timestamps alone are normalized in cross-runtime comparisons.
export function workflowJobFixtures(corpus) {
    const authority = { node: 'fixture-node', principal: 'owner', connection: 'fixture-client' };
    const stage = id => ({ id, function_ref: { application: 'editor', name: id }, bindings: { text: { input: 'text' } },
        preconditions: 'fresh_unique_enabled_targets', postconditions: 'recorded_function_outputs' });
    const definition = { format: 'k-lani-bot-workflow/1', key: 'fixture', revision: 1, inputs: { text: { type: 'string' } },
        applications: { editor: { key: 'fixture-editor', version: '1.0', window: 'main' } }, stages: [stage('open'), stage('write')],
        outputs: { result: { stage: 'write', output: 'value' } } };
    return corpus.cases.map(fixture => {
        const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-job-reference-'));
        let store = new WorkflowStore(root, authority);
        try {
            const job = store.create(fixture.request_id, fixture.fingerprint, definition, fixture.parameters);
            if (fixture.request_conflict) store.create(fixture.request_id, 'b'.repeat(64), definition, fixture.parameters);
            if (fixture.checkpoint) {
                job.completed_stages = ['open'];
                job.outputs = { open: { value: 'confirmed full value' } };
                store.write(job);
            }
            if (fixture.restart) store = new WorkflowStore(root, authority);
            if (fixture.foreign) store = new WorkflowStore(root, { ...authority, node: 'other' });
            const reply = store.read(job.job_ref);
            reply.created_at = '$time';
            reply.updated_at = '$time';
            return { name: fixture.name, reply };
        } catch (error) {
            return { name: fixture.name, reply: { error: error.message } };
        } finally {
            fs.rmSync(root, { recursive: true, force: true });
        }
    });
}
