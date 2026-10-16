import assert from 'node:assert/strict';
import { test } from 'node:test';
import { summarizeThreeRoutes } from './three-route-summary.mjs';

const trial = (route, repetition, success, input, cached, output) => ({ route, repetition, success, dataset: ['1.00'],
    elapsedMs: 100, usage: input === null ? null : { input_tokens: input, cached_input_tokens: cached, output_tokens: output },
    metrics: { calls: [] } });

test('paired savings use the same successful dataset and count cached usage separately', () => {
    const report = { trials: [trial('mcp-cartridge', 0, true, 100, 90, 10), trial('computer-use', 0, true, 400, 300, 20),
        trial('computer-use', 1, false, 900, 800, 30)] };
    const summary = summarizeThreeRoutes(report);
    const savings = summary.pairedSavings.find(entry => entry.baseline === 'computer-use');
    assert.equal(savings.pairedSuccessfulCases, 1);
    assert.equal(savings.totalTokenReduction, 1 - 110 / 420);
    assert.equal(savings.uncachedInputPlusOutputReduction, 1 - 20 / 120);
    const group = summary.groups.find(entry => entry.route === 'computer-use');
    assert.equal(group.totalTokensPerSuccess, 1350);
    assert.equal(group.medianInputTokens, 400);
});

test('an unmeasured failed attempt prevents a complete cost per success claim', () => {
    const report = { trials: [trial('mcp-cartridge', 0, true, 100, 90, 10), trial('mcp-cartridge', 1, false, null, null, null)] };
    const group = summarizeThreeRoutes(report).groups.find(entry => entry.route === 'mcp-cartridge');
    assert.equal(group.unmeasuredTrials, 1);
    assert.equal(group.totalTokensPerSuccess, null);
});

test('a failed attempt is included in paired task completion cost and task medians', () => {
    const report = { trials: [trial('mcp-cartridge', 0, true, 100, 90, 10),
        trial('computer-use', 0, false, 900, 800, 30), trial('computer-use', 0, true, 400, 300, 20)] };
    const summary = summarizeThreeRoutes(report);
    const group = summary.groups.find(entry => entry.route === 'computer-use');
    assert.equal(group.medianTaskInputTokens, 1300);
    assert.equal(group.medianTaskOutputTokens, 50);
    assert.equal(group.medianTaskUncachedInputTokens, 200);
    assert.equal(summary.pairedSavings[0].totalTokenReduction, 1 - 110 / 1350);
});

test('public summaries exclude raw arguments, model answers, local state and internal oracle details', () => {
    const sentinel = 'PRIVATE_RAW_BENCHMARK_CONTENT';
    const record = trial('mcp-cartridge', 0, true, 100, 90, 10);
    record.answer = { detail: sentinel };
    record.nativeUi = { calls: 2, rawArguments: sentinel };
    record.metrics.calls = [{ arguments: { content: sentinel }, imageCount: 0, imageBytes: 0, resultJsonBytes: 1 }];
    record.oracle = { status: 'passed', successful: true, internalState: sentinel };
    const report = { trials: [record], authoring: { rawData: sentinel }, environment: { localState: sentinel } };
    assert.equal(JSON.stringify(summarizeThreeRoutes(report)).includes(sentinel), false);
});

test('hybrid counts semantic dispatches separately from failures and physical fallback', () => {
    const record = trial('bot-hybrid', 0, true, 100, 90, 10);
    record.metrics.serverInfo = { version: 'test-version' };
    record.metrics.calls = [
        { tool: 'find_elements', imageCount: 0, imageBytes: 0, resultJsonBytes: 1 },
        { tool: 'click_element', failed: false, imageCount: 0, imageBytes: 0, resultJsonBytes: 1 },
        { tool: 'type_text', failed: true, imageCount: 0, imageBytes: 0, resultJsonBytes: 1 },
        { tool: 'mouse_click', imageCount: 0, imageBytes: 0, resultJsonBytes: 1 },
        { tool: 'take_screenshot', imageCount: 1, imageBytes: 20, resultJsonBytes: 30 }
    ];
    const summary = summarizeThreeRoutes({ trials: [record], hybridAccess: 'Node accessibility' });
    assert.equal(summary.groups.find(group => group.route === 'bot-hybrid').images, 1);
    assert.equal(summary.trials[0].semanticObservations, 1);
    assert.equal(summary.trials[0].semanticActionDispatches, 1);
    assert.equal(summary.trials[0].semanticActionFailures, 1);
    assert.equal(summary.trials[0].physicalClicks, 1);
    assert.equal(summary.trials[0].toolServerVersion, 'test-version');
});
