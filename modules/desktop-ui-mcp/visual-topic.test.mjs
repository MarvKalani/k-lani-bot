import test from 'node:test';
import assert from 'node:assert/strict';
import { gridCrop, readableCrop, VisualTopic } from './visual-topic.mjs';

test('overview grid selects a bounded native-resolution neighbourhood, including edge cells', () => {
    const geometry = { width: 1280, height: 720 };
    assert.deepEqual(gridCrop('A1', geometry), { left: 0, top: 0, width: 480, height: 270 });
    assert.deepEqual(gridCrop('H8', geometry), { left: 800, top: 450, width: 480, height: 270 });
    assert.throws(() => gridCrop('I9', geometry), /A1/);
});

test('focused write shorthand is bound only to a preceding observed Edit click in the same batch', () => {
    const topic = new VisualTopic({ projection: { references: new Map([['e1', { role: 'Edit' }], ['e2', { role: 'button' }]]) } }, {});
    const steps = topic.normalizeSteps([{ action: 'click', ref: 'e1' }, { action: 'write', text: 'text' }]);
    assert.equal(steps[1].ref, 'e1');
    assert.throws(() => topic.normalizeSteps([{ action: 'write', text: 'text' }]), /requires ref/);
    assert.throws(() => topic.normalizeSteps([{ action: 'click', ref: 'e2' }, { action: 'write', text: 'text' }]), /requires ref/);
});

test('automatic application crop preserves all provided text/control extents and native pixel scale', () => {
    const bounds = [{ x: 100, y: 100, width: 600, height: 200 }, { x: 110, y: 350, width: 300, height: 50 }];
    assert.deepEqual(readableCrop(bounds, { width: 1280, height: 720 }), { left: 76, top: 76, width: 648, height: 348 });
});

test('stale screenshot views and unmarked targets are rejected before any input', async () => {
    let acted = false;
    const topic = new VisualTopic({ call: async () => { acted = true; return { success: true }; } }, {});
    topic.current = { view: 'v2', pid: 10, refs: new Set(['e1']) };
    await assert.rejects(topic.call('screen_act', { pid: 10, view: 'v1', steps: [{ action: 'click', ref: 'e1' }] }), /current/);
    await assert.rejects(topic.call('screen_act', { pid: 10, view: 'v2', steps: [{ action: 'click', ref: 'e2' }] }), /marked/);
    assert.equal(acted, false);
});
