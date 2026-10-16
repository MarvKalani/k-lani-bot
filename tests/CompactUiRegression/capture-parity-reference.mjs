import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DesktopUiGateway } from '../../modules/desktop-ui-mcp/gateway.mjs';
import { LearnedFunctions } from '../../modules/desktop-ui-mcp/learned-functions.mjs';

// This reference compares compilation only. Synthetic transport observations do not establish native application or compatibility acceptance.
// Fixture rows name a window by kind, while the gateway targets carry the same kind as windowKind.
function asTarget(row) {
    if (!row || row.window === undefined) return row;
    const { window, ...target } = row;
    return { ...target, windowKind: window };
}

// Each output reads the tree of its window kind; a fixture observes one window, so every output kind names the same rows.
function outputTrees(outputs, nodes) {
    return new Map(outputs.map(output => [output.selector.window, nodes]));
}

export async function captureFixtures(corpus) {
    const output = [];
    for (const fixture of corpus.cases) {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-capture-reference-'));
        const evidence = '11111111-1111-4111-8111-111111111111';
        const args = structuredClone(fixture.arguments);
        if (args.evidence === '$evidence') args.evidence = evidence;
        let draft;
        const library = new LearnedFunctions({ cartridgeRoot: path.join(directory, 'library'),
            application: { key: 'capture-fixture', version: '1.0', identity_source: 'controlled-test-fixture' } });
        const gateway = {
            demonstration: fixture.no_demonstration ? null : { evidence, pid: 10, before: fixture.before, after: fixture.after,
                steps: fixture.steps?.map(step => ({ ...step, target: asTarget(step.target) })), routes: fixture.steps?.map(step => step.route),
                bindings: fixture.steps?.map(step => asTarget(step.target)) },
            learned: { application: library.application, install: entry => { library.install(entry); draft = entry; } },
            serverInfo: { version: 'fixture' }, platform: 'fixture-platform',
            compatibilityRuntime: { capture: entry => entry.compatibility },
            checkLearningIdentity: () => {}, requireApplicationInstance: async () => {},
            selectWindow: () => ({ pid: 10, window: undefined }), requireWindowInstance: async () => ({ WindowHandle: undefined }),
            readTree: async () => ({ nodes: fixture.after }),
            projection: { target: reference => {
                if (!fixture.references[reference]) throw new Error('Unknown reference for this application. Observe it first.');
                return asTarget(fixture.references[reference]);
            } },
            compileOutput: DesktopUiGateway.prototype.compileOutput
        };
        try {
            if (fixture.kind === 'read') await DesktopUiGateway.prototype.learnRead.call(gateway, args);
            else await DesktopUiGateway.prototype.learnFunction.call(gateway, args);
            const { compatibility, ...compiled } = draft;
            output.push({ name: fixture.name, reply: compiled });
        } catch (error) {
            output.push({ name: fixture.name, reply: { error: error.message } });
        } finally {
            fs.rmSync(directory, { recursive: true, force: true });
        }
    }
    return output;
}

export function outputFixtures(corpus) {
    return corpus.cases.map(fixture => {
        try {
            const after = fixture.after.map(row => ({ ...row, value: row.value?.repeat(fixture.repeat_value ?? 1) ?? null }));
            const outputs = DesktopUiGateway.prototype.verifyOutputs.call({}, { outputs: fixture.outputs }, fixture.parameters,
                outputTrees(fixture.outputs, fixture.before), outputTrees(fixture.outputs, after), fixture.legacy_bound ?? true);
            return { name: fixture.name, reply: { outputs } };
        } catch (error) {
            return { name: fixture.name, reply: { error: error.message } };
        }
    });
}
