import fs from 'node:fs';
import path from 'node:path';
import { runModel } from './model-runner.mjs';
import { referenceCost, lunaRateCard } from './api-reference-cost.mjs';
import { expectedFormInventory, formInventoryMatches, validatePairedCapture } from './form-scan-evaluation.mjs';

// This study measures extraction from identical captured native states, separately from operation and adapter programming.
// Scanner overhead and retained values are compared with the existing compact observation, not an inflated full-tree baseline.
const configuration = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const captures = JSON.parse(fs.readFileSync(process.argv[3], 'utf8')).scans;
for (const capture of captures) validatePairedCapture(capture);
const output = path.resolve(process.argv[4]);
fs.mkdirSync(output, { recursive: true, mode: 0o700 });
const trials = [];
const delivery = configuration.observationDelivery ?? 'mcp';
if (!['mcp', 'inline'].includes(delivery)) throw new Error('Choose inline or mcp observation delivery.');
const nullableString = { type: ['string', 'null'] };
const schema = { type: 'object', properties: {
    fields: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' }, value: nullableString,
        enabled: { type: 'boolean' }, offscreen: { type: 'boolean' }, required: { type: 'null' } },
        required: ['name', 'value', 'enabled', 'offscreen', 'required'], additionalProperties: false } },
    buttons: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' },
        enabled: { type: 'boolean' }, offscreen: { type: 'boolean' }, effect: { type: 'null' } },
        required: ['name', 'enabled', 'offscreen', 'effect'], additionalProperties: false } }
}, required: ['fields', 'buttons'], additionalProperties: false };
const prompt = 'Read the synthetic form using observe_form. Return every text input (native role Edit/entry) and every button '
    + '(native role button/push button), including provider-offscreen or disabled ones. Exclude navigation tabs and other roles. '
    + 'Preserve names and text values exactly; unavailable values are null. Missing enabled/offscreen flags in a compact element '
    + 'mean enabled=true/offscreen=false. Requiredness and button effects are unknown: return null, never infer them from labels. '
    + 'Do not interact with the form or use any other tools. Return only the requested structured inventory.';

for (let index = 0; index < captures.length; index++) {
    const capture = captures[index];
    const routes = index % 2 ? ['scanned', 'compact'] : ['compact', 'scanned'];
    const efforts = index % 2 ? ['medium', 'low'] : ['low', 'medium'];
    for (const effort of efforts) {
        for (const route of routes) {
            const directory = path.join(output, `${index}-${effort}-${route}`);
            fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
            const observation = capture[route];
            const replay = path.join(directory, 'replay.json');
            fs.writeFileSync(replay, JSON.stringify({ observation }), { mode: 0o600 });
            const instructions = delivery === 'inline' ? prompt.replace('Read the synthetic form using observe_form.',
                'Read the synthetic form observation supplied below. Do not call tools; acquisition is already complete.')
                + '\nCaptured observation: ' + JSON.stringify(observation) : prompt;
            const result = await runModel({ ...configuration, model: 'gpt-6-luna', reasoningEffort: effort, answerSchema: schema },
                { command: process.execPath, arguments: [path.resolve('modules/efficiency-benchmark/form-scan-replay.mjs'), replay],
                    route: 'form-' + route, tools: delivery === 'inline' ? [] : ['observe_form'] }, instructions, directory);
            const expected = expectedFormInventory(capture.compact);
            const exact = formInventoryMatches(result.answer, expected);
            const read = delivery === 'inline' || result.metrics?.calls.some(call => call.tool === 'observe_form' && !call.failed);
            const trial = { capture: index, route, effort, success: exact && read && result.exitCode === 0 && !result.unexpectedAction,
                exactInventory: exact, observed: read,
                usage: result.usage, elapsedMs: result.elapsedMs, observationBytes: Buffer.byteLength(JSON.stringify(observation)),
                toolCalls: result.metrics?.calls.length ?? null, images: result.metrics?.calls.reduce((sum, call) => sum + call.imageCount, 0) ?? null,
                usdReference: referenceCost(result.usage), clientToolFailures: result.clientToolFailures };
            trials.push(trial);
            fs.writeFileSync(path.join(output, 'measurements.json'), JSON.stringify(report(), null, 2));
            console.log(JSON.stringify(trial));
        }
    }
}

function report() {
    const groups = [];
    for (const effort of ['low', 'medium']) {
        for (const route of ['compact', 'scanned']) {
            const selected = trials.filter(trial => trial.route === route && trial.effort === effort);
            const measured = selected.filter(trial => trial.usage);
            const successes = selected.filter(trial => trial.success).length;
            const tokens = measured.reduce((sum, trial) => sum + trial.usage.input_tokens + trial.usage.output_tokens, 0);
            groups.push({ effort, route, attempts: selected.length, successes, unknownUsage: selected.length - measured.length,
                tokensPerSuccess: measured.length === selected.length && successes ? tokens / successes : null,
                totalTokens: tokens, usdReference: selected.every(trial => trial.usdReference !== null)
                    ? selected.reduce((sum, trial) => sum + trial.usdReference, 0) : null });
        }
    }
    return { format: 'k-lani-bot-form-scan-extraction-study', version: 1, model: 'gpt-6-luna', delivery, groups, trials, rateCard: lunaRateCard,
        limitations: ['Read-only model extraction from captured real native MCP states, not live operation or adapter code generation.',
            'Three captures: initial page, editor page, edited value. No statistical quality or minimum-effort threshold established.',
            'Capture/scanner development usage is not isolated; native capture uses no inference, but developer tokens are unknown.',
            'Image and schema token breakdowns are unavailable. All measured attempts remain included.',
            'This does not measure full-task routine reuse or prove end-to-end cartridge token savings.'] };
}
