import { captureFunctions } from '../../modules/desktop-ui-mcp/automatic-capture.mjs';
import { DesktopUiGateway } from '../../modules/desktop-ui-mcp/gateway.mjs';
import { captureCompatibility, assessCompatibility } from '../../modules/desktop-ui-mcp/compatibility.mjs';

// Fixture rows name a window by kind, while the gateway targets carry the same kind as windowKind.
function asTarget(row) {
    if (!row || row.window === undefined) return row;
    const { window, ...target } = row;
    return { ...target, windowKind: window };
}

// This host-only library fixture compares first-use decisions and emitted definitions, without claiming durable or native acceptance.
export async function automaticFixtures(corpus) {
    const results = [];
    for (const fixture of corpus.cases) {
        const functions = [];
        const candidates = [];
        const evidence = 'fixture-evidence';
        const learned = { application: { default_topic: 'Use/UI', version: '1.0' }, functions,
            install: entry => { functions.push(entry); const index = candidates.findIndex(item => item.name === entry.name);
                if (index >= 0) candidates.splice(index, 1); },
            saveCandidate: candidate => { if (!candidates.some(item => item.name === candidate.name)) candidates.push(candidate); } };
        const step = { ...fixture.step, target: asTarget(fixture.step.target) };
        const demonstration = { evidence, pid: 10, steps: [step], bindings: [step.target],
            routes: [step.action === 'write' ? 'native-text' : 'semantic'], before: fixture.before, after: fixture.after };
        const gateway = { demonstration, learned, serverInfo: { version: 'fixture' }, platform: 'fixture-platform',
            projection: { references: new Map(Object.entries(fixture.references).map(([ref, row]) => [ref, asTarget(row)])), target: ref => {
                const target = asTarget(fixture.references[ref]);
                if (!target) throw new Error('Unknown reference for this application. Observe it first.');
                return target;
            } }, compatibilityRuntime: { capture: entry => captureCompatibility(entry, fixture.current) },
            compileOutput: DesktopUiGateway.prototype.compileOutput,
            learnFunction: DesktopUiGateway.prototype.learnFunction,
            checkLearningIdentity: (_pid, entry) => {
                if (entry) {
                    const verdict = assessCompatibility(entry, fixture.current);
                    if (verdict.status !== 'reusable') throw new Error('Generated function compatibility changed: ' + verdict.reason + '; revalidate before input.');
                }
            }, requireWindowInstance: async () => undefined };
        const reply = await captureFunctions(gateway, fixture.compact ?? false);
        if (fixture.repeat) reply.second = await captureFunctions(gateway, fixture.compact ?? false);
        reply.definitions = functions.map(({ compatibility, ...draft }) => draft);
        reply.candidates = candidates;
        results.push({ name: fixture.name, reply });
    }

    return results;
}
