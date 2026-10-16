import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { CartridgeRevisions } from './cartridge-revisions.mjs';

function setup(context, checkpoint) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-revision-test-'));
    context.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const file = path.join(root, 'cartridges/programs/editor/1.0/generated-ui.json');
    const state = path.join(root, 'private-recovery');
    const validate = document => {
        if (![1, 2].includes(document.version)) throw new Error('Unsupported newer format.');
    };
    const make = hook => new CartridgeRevisions(file, state, validate, hook);
    const initial = make();
    initial.replace({ version: 1, functions: ['original'] }, 0, null);
    return { file, make, store: make(checkpoint) };
}

test('two replacements of the same revision accept exactly one CAS in the same active directory', context => {
    const { file, store, make } = setup(context);
    const first = store.read();
    const concurrent = make();
    const second = concurrent.read();
    store.replace({ version: 2, functions: ['accepted'] }, first.revision, first.digest);
    assert.throws(() => concurrent.replace({ version: 2, functions: ['stale'] }, second.revision, second.digest), /stale_cartridge_revision/);
    assert.deepEqual(store.read().document.functions, ['accepted']);
    assert.deepEqual(fs.readdirSync(path.dirname(file)), ['generated-ui.json']);
});

test('interruption before atomic replace retains old revision; after replace recovery confirms new revision', context => {
    for (const phase of ['before_replace', 'after_replace']) {
        const { store, make } = setup(context, current => { if (current === phase) throw new Error('Interrupted editor.'); });
        const earlier = store.read();
        assert.throws(() => store.replace({ version: 2, functions: ['new'] }, earlier.revision, earlier.digest), /Interrupted/);
        const restarted = make();
        const recovered = restarted.recover();
        assert.equal(recovered.status, phase === 'before_replace' ? 'previous_revision_retained' : 'committed');
        assert.deepEqual(restarted.read().document.functions, [phase === 'before_replace' ? 'original' : 'new']);
    }
});

test('corrupt temporary state is explicitly recoverable and unknown newer formats never replace the original', context => {
    const { store, make, file } = setup(context, (phase, temporary) => {
        if (phase === 'before_replace') {
            fs.writeFileSync(temporary, 'corrupt');
            throw new Error('Interrupted editor.');
        }
    });
    const previous = fs.readFileSync(file, 'utf8');
    const earlier = store.read();
    assert.throws(() => store.replace({ version: 99 }, earlier.revision, earlier.digest), /newer format/);
    assert.equal(fs.readFileSync(file, 'utf8'), previous);
    assert.throws(() => store.replace({ version: 2 }, earlier.revision, earlier.digest), /Interrupted/);
    assert.throws(() => make().recover(), /recoverable_corrupt_temporary/);
    assert.equal(fs.readFileSync(file, 'utf8'), previous);
});

test('external edits with an unchanged revision still fail CAS and symlink state is rejected', context => {
    const { file, store } = setup(context);
    const previous = store.read();
    fs.writeFileSync(file, JSON.stringify({ version: 1, revision: 1, functions: ['external'] }));
    assert.throws(() => store.replace({ version: 2 }, previous.revision, previous.digest), /stale_cartridge_revision/);
    fs.unlinkSync(file);
    fs.symlinkSync(path.join(path.dirname(file), 'outside'), file);
    assert.throws(() => store.read(), /Invalid or symbolic/);
});

test('actual editor process exit leaves a durable intent and recoverable dead lock before or after replace', context => {
    const module = pathToFileURL(path.resolve('modules/desktop-ui-mcp/cartridge-revisions.mjs')).href;
    for (const phase of ['before_replace', 'after_replace']) {
        const { store, make, file } = setup(context);
        const snapshot = store.read();
        const script = `import { CartridgeRevisions } from ${JSON.stringify(module)};
            const store = new CartridgeRevisions(process.argv[1], process.argv[2], document => {
                if (![1, 2].includes(document.version)) throw new Error('Invalid format');
            }, phase => { if (phase === process.argv[3]) process.exit(74); });
            const before = store.read();
            store.replace({version:2,functions:['process-effect']},before.revision,before.digest);`;
        const exited = spawnSync(process.execPath, ['--input-type=module', '-e', script, file, store.root, phase]);
        assert.equal(exited.status, 74);
        assert.equal(fs.existsSync(store.lock), true);
        const restarted = make();
        restarted.recover();
        assert.equal(fs.existsSync(store.lock), false);
        assert.equal(restarted.read().revision, snapshot.revision + (phase === 'after_replace' ? 1 : 0));
    }
});
