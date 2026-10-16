import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { WorkflowStore } from './workflow-store.mjs';

function setup(context) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-workflow-state-'));
    context.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const store = new WorkflowStore(root, { node: 'test', principal: 'owner', connection: 'authorized-client' });
    return { root, store };
}

test('private durable jobs deduplicate only client job creation, bind authority and survive a store restart', context => {
    const { root, store } = setup(context);
    const job = store.create('request-one', 'digest', { key: 'test', revision: 1 }, { value: 'PRIVATE_RUNTIME_VALUE' });
    assert.equal(store.create('request-one', 'digest', {}, {}).job_ref, job.job_ref);
    assert.throws(() => store.create('request-one', 'different', {}, {}), /different content/);
    const restarted = new WorkflowStore(root, { node: 'test', principal: 'owner', connection: 'authorized-client' });
    assert.equal(restarted.read(job.job_ref).parameters.value, 'PRIVATE_RUNTIME_VALUE');
    const reordered = new WorkflowStore(root, { connection: 'authorized-client', principal: 'owner', node: 'test' });
    assert.equal(reordered.read(job.job_ref).job_ref, job.job_ref);
    const foreign = new WorkflowStore(root, { node: 'other', principal: 'owner', connection: 'authorized-client' });
    assert.throws(() => foreign.read(job.job_ref), /foreign/);
    assert.throws(() => store.read('../outside'), /unknown_job/);
    assert.equal(fs.statSync(store.filename(job.job_ref)).mode & 0o077, 0);
});

test('concurrent job actions serialize; checkpoints retain confirmed stages instead of overwriting them', async context => {
    const { store } = setup(context);
    const job = store.create('request', 'digest', {}, {});
    await Promise.all([store.serialized(job.job_ref, async current => {
        current.completed_stages.push('first');
        await new Promise(resolve => setTimeout(resolve, 5));
        store.write(current);
    }), store.serialized(job.job_ref, current => {
        assert.deepEqual(current.completed_stages, ['first']);
        current.completed_stages.push('second');
        store.write(current);
    })]);
    assert.deepEqual(store.read(job.job_ref).completed_stages, ['first', 'second']);
});

test('capacity never silently deletes active jobs, and symbolic state files are rejected', context => {
    const { root, store } = setup(context);
    for (let index = 0; index < 100; index++) store.create('request-' + index, 'digest', {}, {});
    assert.throws(() => store.create('overflow', 'digest', {}, {}), /workflow_store_full/);
    assert.equal(fs.readdirSync(root).filter(name => name.endsWith('.json')).length, 100);
    const job = store.create('request-0', 'digest', {}, {});
    const file = store.filename(job.job_ref);
    fs.unlinkSync(file);
    fs.symlinkSync(path.join(root, 'missing'), file);
    assert.throws(() => store.read(job.job_ref), /unknown_job/);
});

test('a proven dead lock owner is recovered; live or foreign owners are never removed', async context => {
    const { store } = setup(context);
    const job = store.create('request', 'digest', {}, {});
    const file = store.filename(job.job_ref) + '.lock';
    fs.writeFileSync(file, JSON.stringify({ pid: 2147483647, process_start: 'dead', authority: store.authority }));
    await store.serialized(job.job_ref, current => assert.equal(current.job_ref, job.job_ref));
    assert.equal(fs.existsSync(file), false);
    fs.writeFileSync(file, JSON.stringify({ pid: process.pid, authority: 'foreign' }));
    await assert.rejects(store.serialized(job.job_ref, () => {}), /workflow_lock_invalid/);
    assert.equal(fs.existsSync(file), true);
});

test('a checkpoint and lock left by an exited gateway process recover without losing completed stages', async context => {
    const { root, store } = setup(context);
    const moduleUrl = pathToFileURL(path.resolve('modules/desktop-ui-mcp/workflow-store.mjs')).href;
    const script = `import fs from 'node:fs';
        import { WorkflowStore } from ${JSON.stringify(moduleUrl)};
        const store = new WorkflowStore(process.argv[1], { node: 'test', principal: 'owner', connection: 'authorized-client' });
        const job = store.create('process-crash', 'digest', {}, {});
        job.completed_stages.push('confirmed');
        store.write(job);
        fs.writeFileSync(store.filename(job.job_ref) + '.lock', JSON.stringify({
            pid: process.pid, process_start: 'exited', authority: store.authority }));
        process.stdout.write(job.job_ref);`;
    const reference = execFileSync(process.execPath, ['--input-type=module', '-e', script, root], { encoding: 'utf8' });
    await store.serialized(reference, current => assert.deepEqual(current.completed_stages, ['confirmed']));
    assert.equal(fs.existsSync(store.filename(reference) + '.lock'), false);
});
