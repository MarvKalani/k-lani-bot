import { test } from 'node:test';
import assert from 'node:assert/strict';
import { summarizeScreenshotStudy } from './screenshot-study-summary.mjs';

test('timeouts cannot make an image variant look free or completed', () => {
    const report = { model: 'gpt-6-luna', reasoningEffort: 'medium', trials: [
        { route: 'half', success: false, usage: null, elapsedMs: 300000, metrics: { calls: [] } },
    ] };
    const group = summarizeScreenshotStudy(report).groups[0];
    assert.equal(group.successes, 0);
    assert.equal(group.measuredAttempts, 0);
    assert.equal(group.totalTokens, null);
    assert.equal(group.totalUsdReference, null);
    assert.equal(group.medianModelDecisionMsBeforeClick, null);
});
