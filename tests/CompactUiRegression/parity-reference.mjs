import { workflowDefinitions } from '../../modules/desktop-ui-mcp/workflow-runtime.mjs';
import { compatibilityDefinitions } from '../../modules/desktop-ui-mcp/compatibility-runtime.mjs';
import { uiTools, generatedCartridgeTools } from '../../modules/desktop-ui-mcp/protocol.mjs';
import { resultDefinition } from '../../modules/desktop-ui-mcp/result-store.mjs';
import { formScanDefinition, formReadDefinition } from '../../modules/desktop-ui-mcp/form-scanner.mjs';
import { learnDefinition, preflightDefinition, readDefinition, summarizeListedFunctions, stepLabel,
    selectProcessHint, storedFunctionsSentence, noStoredFunctionsSentence } from '../../modules/desktop-ui-mcp/learned-functions.mjs';
import fs from 'node:fs';
import { DesktopUiGateway, requireInstanceWindow, usageHint, compactInstructions, pidRefusal } from '../../modules/desktop-ui-mcp/gateway.mjs';
import { ResultStore } from '../../modules/desktop-ui-mcp/result-store.mjs';
import { formatResult, validateResultOptions } from '../../modules/desktop-ui-mcp/result-format.mjs';
import { ViewStore } from '../../modules/desktop-ui-mcp/view-store.mjs';
import { UiProjection, parseNativeTree } from '../../modules/desktop-ui-mcp/projection.mjs';
import { bindWindowState, buildWindowState, diffWindowStates } from '../../modules/desktop-ui-mcp/window-state.mjs';
import { formFixtures } from './form-parity-reference.mjs';
import { formDefinitions, validateFormArguments } from '../../modules/desktop-ui-mcp/form-contract.mjs';
import { discoveryFixtures } from './discovery-parity-reference.mjs';
import { roleFamilyFixtures } from './role-family-parity-reference.mjs';
import { captureFixtures, outputFixtures } from './capture-parity-reference.mjs';
import { compatibilityFixtures, nativeApiFixtures } from './compatibility-parity-reference.mjs';
import { automaticFixtures } from './automatic-parity-reference.mjs';
import { functionDocumentFixtures } from './function-document-parity-reference.mjs';
import { receiptFixtures } from './receipt-parity-reference.mjs';
import { workflowDefinitionFixtures } from './workflow-definition-parity-reference.mjs';
import { workflowJobFixtures } from './workflow-job-parity-reference.mjs';
import { workflowEngineFixtures } from './workflow-engine-parity-reference.mjs';

// Both runtimes receive the same synthetic corpus; no model provider or application output is simulated by this reference check.
const corpus = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (corpus.kind === 'window-state') {
    await writeOutput(corpus.cases.map(testCase => {
        const outcome = buildWindowState(parseNativeTree(testCase.tree), {
            windowHandle: testCase.window.handle, kind: testCase.window.kind,
            maxChars: testCase.options?.max_chars, cursor: testCase.options?.cursor,
        });
        return { name: testCase.name, result: 'error' in outcome ? outcome : outcome.value };
    }));
    process.exit(0);
}
if (corpus.kind === 'window-diff') {
    await writeOutput(corpus.cases.map(windowDiffCase));
    process.exit(0);
}
if (corpus.kind === 'compact-descriptors') {
    await writeOutput({ tools: [...uiTools, ...generatedCartridgeTools], functions: [resultDefinition, formScanDefinition, formReadDefinition, learnDefinition, preflightDefinition, ...formDefinitions, ...compatibilityDefinitions, ...workflowDefinitions, readDefinition] });
    process.exit(0);
}
if (corpus.kind === 'step-labels') {
    await writeOutput(corpus.cases.map(testCase => ({ name: testCase.name, labels: testCase.steps.map(stepLabel) })));
    process.exit(0);
}
if (corpus.kind === 'stored-listing') {
    await writeOutput(storedListingFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'window-selection') {
    await writeOutput(await windowSelectionOutput(corpus));
    process.exit(0);
}
if (corpus.kind === 'window-instance') {
    await writeOutput(windowInstanceOutput(corpus));
    process.exit(0);
}
if (corpus.kind === 'workflow-engine') {
    await writeOutput(await workflowEngineFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'workflow-jobs') {
    await writeOutput(workflowJobFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'workflow-definitions') {
    await writeOutput(workflowDefinitionFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'receipts') {
    await writeOutput(receiptFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'function-documents') {
    await writeOutput(functionDocumentFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'automatic-capture') {
    await writeOutput(await automaticFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'compatibility') {
    await writeOutput(compatibilityFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'native-api') {
    await writeOutput(nativeApiFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'output-verification') {
    await writeOutput(outputFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'capture') {
    await writeOutput(await captureFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'role-families') {
    await writeOutput(roleFamilyFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'discovery') {
    await writeOutput(discoveryFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'forms') {
    await writeOutput(await formFixtures(corpus));
    process.exit(0);
}
if (corpus.kind === 'form-arguments') {
    await writeOutput(corpus.cases.map(fixture => {
        try {
            const args = structuredClone(fixture.arguments);
            if (fixture.repeat_value) args.value = args.value.repeat(fixture.repeat_value);
            validateFormArguments(fixture.function, args);
            return { name: fixture.name, accepted: true };
        } catch (error) {
            return { name: fixture.name, error: error.message };
        }
    }));
    process.exit(0);
}
const replies = [];
for (const fixture of corpus.cases ?? []) {
    if (corpus.kind === 'result-format') {
        replies.push(formatFixture(fixture));
        continue;
    }
    const store = new ResultStore();
    const reference = store.put(fixture.owner, fixture.outputs, fixture.debug);
    const pages = [];
    for (const query of fixture.queries) {
        try {
            const args = { evidence_ref: reference, ...query.arguments };
            pages.push(store.read(query.request_owner ?? fixture.owner, args));
        } catch (error) {
            pages.push({ error: error.message });
        }
    }
    replies.push({ name: fixture.name, pages });
}
const output = corpus.kind === 'view-pages' ? viewFixtures(corpus)
    : corpus.kind === 'projection' ? projectionFixtures(corpus) : replies;
await writeOutput(output);

// The listing bounds and the words of each reply that names stored functions; both gateways must send these exact values.
function windowDiffCase(testCase) {
    const trees = testCase.trees.map(tree => parseNativeTree(tree));
    const options = { windowHandle: testCase.window.handle, kind: testCase.window.kind, maxChars: testCase.options?.max_chars };
    const steps = trees.slice(1).map((tree, index) => diffWindowStates(trees[index], tree, options).value);
    const total = diffWindowStates(trees[0], trees[trees.length - 1], options).value;
    const bindingMap = bindWindowState(trees[0]).value;
    const bindings = Object.fromEntries(Object.keys(testCase.expect_bindings).map(path => [path, bindingMap.get(path)]));
    return { name: testCase.name, steps, total, bindings };
}

function storedListingFixtures(corpus) {
    const summaries = corpus.cases.map(testCase => {
        const functions = testCase.functions.map(entry => ({ name: entry.name, description: entry.description,
            parameters: entry.parameters ?? [], steps: entry.steps ?? [] }));
        const summary = summarizeListedFunctions(functions);
        return { name: testCase.name, functions: summary.functions, more_functions: summary.more };
    });

    return { summaries, texts: { usage: usageHint, select: selectProcessHint, pid: pidRefusal, instructions: compactInstructions,
        stored_functions: storedFunctionsSentence, no_stored_functions: noStoredFunctionsSentence } };
}

// Each case starts a new gateway; every info step replaces the windows that Info lists, as a fresh ui_info reply does.
async function windowSelectionOutput(corpus) {
    const results = [];
    for (const testCase of corpus.cases) {
        let listed = [];
        const client = { call: async tool => ({ result: { structuredContent: tool === 'list_windows'
            ? { success: true, windows: listed } : { success: true, capabilities: { nodeId: 'fixture-node', platform: 'linux-x11-atspi' } } } }) };
        const gateway = new DesktopUiGateway(client);
        for (const step of testCase.steps) {
            if (step.info) {
                listed = step.info.map(row => ({ ProcessId: row.pid, WindowHandle: row.handle, Kind: row.kind, Title: 'Fixture',
                    ProcessStartedAtUtc: 'start' }));
                await gateway.info();
            } else {
                results.push(windowSelectionOutcome(gateway, step.arguments));
            }
        }
    }
    return results;
}

// The learning rule answers each proved or refused window the same way as the compact gateway's RequireInstance.
function windowInstanceOutput(corpus) {
    return corpus.cases.flatMap(testCase => {
        const windows = testCase.windows.map(row => ({ ProcessId: row.pid, WindowHandle: row.handle, Kind: row.kind,
            Title: 'Fixture', ProcessStartedAtUtc: row.started }));

        return testCase.checks.map(check => windowInstanceOutcome(windows, check));
    });
}

function windowInstanceOutcome(windows, check) {
    try {
        const window = requireInstanceWindow(windows.filter(row => row.ProcessId === check.pid), check.window ?? undefined);
        return { ok: true, handle: window.WindowHandle };
    } catch (error) {
        return { ok: false, error: error.message };
    }
}

function windowSelectionOutcome(gateway, args) {
    try {
        const selection = gateway.selectWindow(args);
        return { ok: true, pid: selection.pid, window: selection.window ?? null };
    } catch {
        return { ok: false };
    }
}

async function writeOutput(value) {
    await new Promise((resolve, reject) => {
        process.stdout.write(JSON.stringify(value), error => {
            if (error) reject(error);
            else resolve();
        });
    });
}

function projectionFixtures(corpus) {
    const projection = new UiProjection();
    const actual = new Map();
    return corpus.steps.map(step => {
        const options = { ...step.options };
        if (step.since) options.since_view = actual.get(step.since).view_ref;
        try {
            const tree = step.tree ?? step.nodes.map(node => ' '.repeat(node.depth ?? 0) + `[${node.id}] ${node.role} `
                + `${JSON.stringify(node.name)} (autoId: ${JSON.stringify(node.autoId ?? '')}, val: ${JSON.stringify(node.value ?? '')}, `
                + `enabled: ${node.enabled === false ? 'False' : 'True'}, offscreen: ${node.offscreen ? 'True' : 'False'})`).join('\n');
            const reply = projection.observe(step.pid, parseNativeTree(tree), options);
            actual.set(step.name, reply);
            return { name: step.name, reply: normalizeViewReferences(reply) };
        } catch (error) {
            return { name: step.name, error: error.message };
        }
    });
}

function viewFixtures(corpus) {
    const store = new ViewStore();
    const actual = new Map();
    return corpus.steps.map(step => {
        const context = store.context(step.context);
        const rows = step.records ?? Array.from({ length: step.count }, (_, index) => ({ ref: 'e' + index, name: 'Field ' + index }));
        const options = { ...step.options };
        if (step.since) options.since_view = actual.get(step.since).view_ref;
        if (step.cursor) options.cursor = actual.get(step.cursor).next_cursor;
        try {
            const reply = store.page(context, rows, step.stamp, options);
            actual.set(step.name, reply);
            return { name: step.name, reply: normalizeViewReferences(reply) };
        } catch (error) {
            return { name: step.name, error: error.message };
        }
    });
}

function normalizeViewReferences(reply) {
    return { ...reply, view_ref: '$view', context_ref: '$context', next_cursor: reply.next_cursor ? '$cursor' : null };
}

function formatFixture(fixture) {
    const result = structuredClone(fixture.result);
    for (const name of Object.keys(result.outputs ?? {})) {
        result.outputs[name] = result.outputs[name].repeat(fixture.repeat_outputs ?? 1);
    }
    try {
        validateResultOptions(fixture.options, Object.keys(result.outputs ?? {}));
        const store = new ResultStore();
        const reply = formatResult(store, 'node', result, fixture.options, fixture.checks ?? {}, fixture.scan ?? null);
        const debug = reply.evidence_ref ? store.read('node', { evidence_ref: reply.evidence_ref, section: 'debug', limit: 8000 }) : null;
        return { name: fixture.name, reply: normalizeReferences(reply), ...(debug ? { debug_page: debug } : {}) };
    } catch (error) {
        return { name: fixture.name, error: error.message };
    }
}

function normalizeReferences(value) {
    if (Array.isArray(value)) return value.map(normalizeReferences);
    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([name, item]) =>
            [name, name === 'evidence_ref' ? '$evidence' : normalizeReferences(item)]));
    }
    return value;
}
