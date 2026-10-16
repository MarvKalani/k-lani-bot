import test from 'node:test';
import assert from 'node:assert/strict';
import { ResultStore } from './result-store.mjs';
import { formatResult } from './result-format.mjs';

test('oversized evidence is an explicit delivery failure, retaining completed input and refusing silent clipping', () => {
    const store = new ResultStore();
    const result = { success: true, completed_steps: 1, outputs: { text: 'x'.repeat(600000) } };
    const reply = formatResult(store, 'a', result, { result_mode: 'values' });
    assert.equal(reply.success, false);
    assert.equal(reply.error, 'evidence_too_large');
    assert.equal(reply.completed_steps, 1);
    assert.equal(reply.evidence_ref, undefined);
    assert.equal(store.bytes, 0);
});

test('debug keeps small replies inline and caps large scans to an index with retrievable exact JSON', () => {
    const store = new ResultStore();
    const small = { success: true, completed_steps: 0 };
    assert.equal(formatResult(store, 'a', small, { result_mode: 'debug' }).completed_steps, 0);
    const fields = Array.from({ length: 200 }, (_, index) => ({ name: 'Field ' + index, value: 'ä😀'.repeat(100) }));
    const original = { success: true, form: 'test', fields, change: { added: fields.map(field => field.name) } };
    const reply = formatResult(store, 'a', original, { result_mode: 'debug' }, {}, { field_count: 200 });
    assert.ok(Buffer.byteLength(JSON.stringify(reply)) <= 8192);
    assert.equal(reply.field_count, 200);
    assert.equal(reply.truncated, true);
    let text = '';
    let offset = 0;
    do {
        const page = store.read('a', { evidence_ref: reply.evidence_ref, section: 'debug', offset, limit: 8000 });
        text += page.text;
        offset = page.next_offset;
        if (page.complete) break;
    } while (true);
    assert.deepEqual(JSON.parse(text), original);
});
