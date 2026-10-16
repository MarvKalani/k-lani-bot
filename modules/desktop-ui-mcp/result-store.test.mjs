import test from 'node:test';
import assert from 'node:assert/strict';
import { ResultStore, resultLimits } from './result-store.mjs';

test('Unicode pages reconstruct exact unknown content and debug without splitting surrogate pairs', () => {
    const store = new ResultStore();
    const text = 'Größe😀\n'.repeat(1600);
    const evidence_ref = store.put('node-a', { document: text }, JSON.stringify({ text }));
    let offset = 0;
    let reconstructed = '';
    do {
        const page = store.read('node-a', { evidence_ref, section: 'outputs/document', offset });
        reconstructed += page.text;
        offset = page.next_offset;
        assert.equal(Array.from(page.text).length, Math.min(2000, page.total_characters - page.offset));
        if (page.complete) break;
    } while (true);
    assert.equal(reconstructed, text);
    const debug = store.read('node-a', { evidence_ref, section: 'debug', limit: 8000 });
    assert.equal(debug.text, Array.from(JSON.stringify({ text })).slice(0, 8000).join(''));
    assert.deepEqual(store.read('node-a', { evidence_ref, section: 'outputs/document', offset }),
        { text: '', offset, next_offset: offset, complete: true, total_characters: offset });
});

test('references are node/session bound, opaque, expire exactly at the TTL and clear on disconnect', () => {
    let now = 100;
    const store = new ResultStore(() => now);
    const evidence_ref = store.put('a', { value: 'private' }, '{}');
    const args = { evidence_ref, section: 'outputs/value' };
    assert.throws(() => store.read('b', args), /evidence_not_found/);
    assert.throws(() => new ResultStore().read('a', args), /evidence_not_found/);
    assert.throws(() => store.read('a', { ...args, evidence_ref: '/some/file' }), /evidence_not_found/);
    now += resultLimits.ttl - 1;
    assert.equal(store.read('a', args).text, 'private');
    now++;
    assert.throws(() => store.read('a', args), /evidence_expired/);
    assert.throws(() => store.read('b', args), /evidence_not_found/);
    assert.equal(store.bytes, 0);
    store.clear();
    assert.throws(() => store.read('a', args), /evidence_not_found/);
});

test('oldest insertion is evicted at entry and UTF-8 byte budgets, reads do not extend lifetime', () => {
    const store = new ResultStore();
    const first = store.put('a', {}, 'first');
    for (let index = 0; index < 32; index++) store.put('a', {}, String(index));
    assert.equal(store.entries.size, 32);
    assert.throws(() => store.read('a', { evidence_ref: first, section: 'debug' }), /evidence_not_found/);
    store.clear();
    const full = 'ä'.repeat(resultLimits.entryBytes / 2);
    const references = Array.from({ length: 5 }, () => store.put('a', {}, full));
    assert.equal(store.bytes, resultLimits.bytes);
    assert.equal(store.entries.size, 4);
    assert.throws(() => store.read('a', { evidence_ref: references[0], section: 'debug' }), /evidence_not_found/);
    assert.throws(() => store.put('a', {}, full + 'x'), /evidence_too_large/);
    assert.equal(store.bytes, resultLimits.bytes);
});

test('invalid names, offsets, sizes and extra arguments fail explicitly', () => {
    const store = new ResultStore();
    const evidence_ref = store.put('a', { value: 'abc' }, '{}');
    const args = { evidence_ref, section: 'outputs/value' };
    for (const change of [{ offset: -1 }, { offset: 1.5 }, { offset: 4 }, { limit: 0 }, { limit: 8001 },
        { section: 'outputs/unknown' }, { section: '../debug' }, { node: 'b' }, { limit: null }, { offset: null }]) {
        assert.throws(() => store.read('a', { ...args, ...change }), /Invalid|out of range/);
    }
});
