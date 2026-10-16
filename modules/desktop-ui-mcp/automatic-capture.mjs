import { createHash } from 'node:crypto';
import { isGeneratedName } from './learned-functions.mjs';

// First-use actions create verified functions without an extra schema-writing or learning round trip.
// Other actions expose semantic material as unverified candidates; dispatch is never presented as verified completion.
export async function captureFunctions(gateway, compact = false) {
    const demonstration = gateway.demonstration;
    const results = [];
    for (let index = 0; index < demonstration.steps.length; index++) {
        const result = await captureStep(gateway, demonstration, index, compact);
        if (result) results.push(result);
    }
    gateway.demonstration = demonstration;
    return { functions: results, ...(!compact || results.some(result => result.verified) ? {
        instruction: 'Load the selected function schema through cartridge_browse; reuse it through cartridge_call.' } : {}) };
}

// Generated names stay out of the reply the model reads. A verified generated function remains reachable by topic through
// cartridge_browse, and a candidate keeps its status and reason without a name; the instruction names only listed functions.
export function announcedCaptures(reply) {
    const functions = reply.functions.flatMap(announcedCapture);
    const { instruction, ...rest } = reply;
    const named = functions.some(item => item.function !== undefined);

    return { ...rest, functions, ...(named && instruction !== undefined ? { instruction } : {}) };
}

function announcedCapture(item) {
    if (item.function === undefined || !isGeneratedName(item.function)) return [item];
    if (item.status === 'available') return [];
    const candidate = { ...item };
    delete candidate.function;

    return [candidate];
}

async function captureStep(gateway, demonstration, index, compact) {
    const step = demonstration.steps[index];
    const target = demonstration.bindings[index];
    if (!target) return null;
    const name = functionName(target, step.action);
    try {
        const existing = gateway.learned.functions.find(entry => entry.name === name
            || isGeneratedName(entry.name) && capturedSelectorMatches(entry.steps[0]?.selector, target, step.action, entry.steps[0]?.action));
        if (existing) {
            gateway.checkLearningIdentity(demonstration.pid, existing);
            if (!capturedSelectorMatches(existing.steps[0]?.selector, target, step.action, existing.steps[0]?.action)) {
                throw new Error('Captured function scope changed; revalidate its existing definition before reuse.');
            }
            return compact ? null : { function: existing.name, status: 'available', reused_definition: true };
        }
        const output = automaticOutput(gateway, target, step);
        gateway.demonstration = { ...demonstration, steps: [step], bindings: [target], routes: [demonstration.routes[index]] };
        const learned = await gateway.learnFunction({ evidence: demonstration.evidence, name,
            topic: gateway.learned.application.default_topic ?? 'Use/UI',
            parameters: step.action === 'write' ? ['text'] : [], outputs: [output] });
        return { function: learned.function, status: 'available', verified: true };
    } catch (error) {
        return captureCandidate(gateway, step, target, name, error, compact);
    }
}

function captureCandidate(gateway, step, target, name, error, compact) {
    const selector = { role: target.role, name: target.name, autoId: target.autoId, window: target.windowKind,
        ...(target.containerPath ? { containerPath: target.containerPath } : {}) };
    try {
        if (!gateway.learned.functions.some(entry => entry.name === name)) {
            gateway.learned.saveCandidate({ name, topic: gateway.learned.application.default_topic ?? 'Use/UI',
                action: step.action, selector, reason: error.message });
        }
    } catch (storageError) {
        return { ...(!compact ? { function: name } : {}), status: 'storage_failed', reason: storageError.message };
    }
    return { ...(!compact ? { function: name, material: { action: step.action, selector } } : {}),
        status: 'needs_verification', reason: error.message };
}

function capturedSelectorMatches(selector, target, action, recordedAction) {
    return Boolean(selector && action === recordedAction && selector.role === target.role && selector.window === target.windowKind
        && (target.autoId ? selector.autoId === target.autoId : !selector.autoId && selector.name === target.name)
        && JSON.stringify(selector.containerPath ?? []) === JSON.stringify(target.containerPath ?? []));
}

function automaticOutput(gateway, target, step) {
    if (step.action === 'write') return { name: 'value', ref: step.ref, field: 'value' };
    if (step.action !== 'click' || !/tab/i.test(target.role)) {
        throw new Error('A goal-specific output check is still required; the semantic target is available as material.');
    }
    const match = [...gateway.projection.references.entries()].filter(([, identity]) => identity.pid === target.pid
        && identity.windowKind === target.windowKind && identity.role === 'heading' && identity.name === target.name);
    if (match.length !== 1) throw new Error('No unambiguous matching page heading verifies this navigation.');
    return { name: 'page', ref: match[0][0], field: 'name' };
}

function functionName(target, action) {
    const caption = (target.autoId || target.name).normalize('NFKD').replace(/[\u0300-\u036f]/g, '')
        .toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '').slice(0, 42) || 'element';
    const identity = { role: target.role, ...(target.autoId ? { autoId: target.autoId } : { name: target.name, window: target.windowKind }),
        containerPath: target.containerPath ?? [] };
    const hash = createHash('sha256').update(JSON.stringify(identity)).digest('hex').slice(0, 8);
    return `ui.${caption}.${action}.${hash}`;
}
