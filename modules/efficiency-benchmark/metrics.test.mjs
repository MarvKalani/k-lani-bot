import test from 'node:test';
import assert from 'node:assert/strict';
import { summarizeTrials } from './metrics.mjs';

test('failed attempts are included in cost per successful completion', () => {
    const trials = [true, false].map(success => ({ task: 'edit', route: 'mcp-cartridge', success, elapsedMs: 100,
        usage: { input_tokens: 1000, cached_input_tokens: 250, output_tokens: 20 }, metrics: { calls: [] } }));
    const summary = summarizeTrials(trials)[0];
    assert.equal(summary.successes, 1);
    assert.equal(summary.totalInputTokens, 2000);
    assert.equal(summary.inputTokensPerSuccess, 2000);
    assert.equal(summary.medianUncachedInputTokens, 750);
});

test('a failed run with missing usage does not become a zero-token attempt', () => {
    const trials = [true, false].map(success => ({ task: 'read', route: 'screenshot-adapter', success, elapsedMs: 100,
        usage: success ? { input_tokens: 1000, cached_input_tokens: 0, output_tokens: 20 } : null, metrics: { calls: [] } }));
    const summary = summarizeTrials(trials).find(group => group.route === 'screenshot-adapter');
    assert.equal(summary.unmeasuredTrials, 1);
    assert.equal(summary.inputTokensPerSuccess, null);
});
