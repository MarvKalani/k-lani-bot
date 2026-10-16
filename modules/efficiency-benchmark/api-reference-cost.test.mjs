import { test } from 'node:test';
import assert from 'node:assert/strict';
import { referenceCost, routeReferenceCosts } from './api-reference-cost.mjs';

test('cached input is deducted from regular input and reasoning output is not counted twice', () => {
    const usage = { input_tokens: 1000000, cached_input_tokens: 500000, output_tokens: 100000,
        reasoning_output_tokens: 50000, cache_write_input_tokens: 0 };
    assert.equal(referenceCost(usage), 0.105);
});

test('cumulative usage does not automatically select long-context pricing and failed attempts remain billable', () => {
    const usage = { input_tokens: 500000, cached_input_tokens: 0, output_tokens: 0 };
    assert.equal(referenceCost(usage), 0.05);
    const costs = routeReferenceCosts([{ route: 'test', success: false, usage }, { route: 'test', success: true, usage }], 'test');
    assert.equal(costs.usdPerSuccessReference, 0.10);
    assert.equal(costs.actualChargedUsd, null);
});
