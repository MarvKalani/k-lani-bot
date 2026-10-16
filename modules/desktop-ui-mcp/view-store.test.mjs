import test from 'node:test';
import assert from 'node:assert/strict';
import { ViewStore, viewLimits } from './view-store.mjs';

const rows = count => Array.from({ length: count }, (_, index) => ({ ref: 'e' + index, name: 'Field ' + index }));

test('250 controls are returned exactly once in one completed view; changed and foreign cursors are refused', () => {
    const store = new ViewStore();
    const context = store.context('a');
    const records = rows(250);
    let reply = store.page(context, records, 'first', {});
    const reference = reply.view_ref;
    const collected = [...reply.elements];
    while (reply.next_cursor) {
        reply = store.page(context, records, 'first', { cursor: reply.next_cursor });
        assert.equal(reply.view_ref, reference);
        collected.push(...reply.elements);
    }
    assert.deepEqual(collected, records);
    assert.deepEqual(store.page(context, records, 'first', { since_view: reference }).changed, []);
    const pending = store.page(context, records, 'first', { full: true });
    assert.throws(() => store.page(store.context('b'), records, 'first', { cursor: pending.next_cursor }), /stale_cursor/);
    assert.throws(() => store.page(context, records, 'changed', { cursor: pending.next_cursor }), /stale_cursor/);
});

test('TTL, LRU context and total-control budgets remove old data and reset invalidates cursors', () => {
    let now = 0;
    const store = new ViewStore(() => now);
    const first = store.context('first');
    const pending = store.page(first, rows(201), 'a', {});
    store.page(first, rows(201), 'a', { full: true });
    assert.throws(() => store.page(first, rows(201), 'a', { cursor: pending.next_cursor }), /stale_cursor/);
    for (let index = 0; index < 31; index++) store.context(String(index));
    store.context('first');
    store.context('new');
    assert.equal(store.contexts.has('0'), false);
    assert.equal(store.contexts.has('first'), true);
    store.page(store.context('big'), rows(6000), 'b', { limit: 200 });
    store.page(store.context('other-big'), rows(6000), 'c', { limit: 200 });
    assert.equal(store.contexts.has('big'), false);
    assert.throws(() => store.page(store.context('oversize'), rows(10001), 'd', {}), /view_scope_too_large/);
    now += viewLimits.ttl;
    store.expire();
    assert.equal(store.contexts.size, 0);
    assert.equal(store.cursors.size, 0);
    store.clear();
});

test('incomplete views cannot authorize a delta and unknown/filter baselines return an explicit full reason', () => {
    const store = new ViewStore();
    const a = store.context('a');
    const pending = store.page(a, rows(250), 'one', {});
    const incomplete = store.page(a, rows(250), 'one', { since_view: pending.view_ref });
    assert.equal(incomplete.kind, 'full');
    assert.match(incomplete.baseline_reason, /incomplete/);
    const complete = store.page(store.context('b'), rows(1), 'two', {});
    const foreign = store.page(a, rows(1), 'three', { since_view: complete.view_ref });
    assert.equal(foreign.baseline_reason, 'context_mismatch');
});
