import { buildWindowState, bindWindowState, diffWindowStates } from './window-state.mjs';
import { semanticTarget } from './projection.mjs';

// Member kinds from window-state.mjs: set writes a text, number or secret member, call clicks a method or toggle.
const settableKinds = new Set(['text', 'secret', 'number']);
const readBackKinds = new Set(['text', 'number']);
const callableKinds = new Set(['method', 'toggle']);
const minimumSteps = 1;
const maximumSteps = 12;
const invalidStepsHint = 'Each step is {set: path, value: text}, {call: path} or {wait: path, until, value?, timeout_ms?}.';
const shapeHint = 'Unnamed fields are named by position; pass the shape of the state read you used.';
const settableHint = 'set accepts text, number and password properties; choice and other members cannot be set yet.';
const callableHint = 'call accepts methods only.';
const retryHint = 'Do not automatically retry mutations or replay completed steps; read the state again.';
const waitNotMetHint = 'The window did not reach the awaited state; read the state again.';
const dialogOpenedHint = 'A dialog opened; change it with ui_apply on its window.';
const waitConditions = new Set(['exists', 'visible', 'hidden', 'absent', 'enabled', 'disabled', 'value']);
const absentConditions = new Set(['hidden', 'absent']);
const waitKeys = ['wait', 'until', 'value', 'timeout_ms'];
const minimumWaitMilliseconds = 1;
const defaultWaitMilliseconds = 5000;
const maximumWaitMilliseconds = 30000;
const waitPollMilliseconds = 250;

// Runs one ui_apply request for DesktopUiGateway.call: checks every step before any input, then performs each step
// against a fresh read of the window, reads typed values back and reports what each step changed.
// One instance serves one request, so the operation decision and the dispatch counts never leak into the next request.
export class WindowApply {
    constructor(gateway) {
        this.gateway = gateway;
    }

    async run(args) {
        const steps = parseSteps(args.steps);
        if (steps === null) return { success: false, error: 'invalid_steps', hint: invalidStepsHint };
        this.gateway.operationDecision = args.decision ?? null;
        this.gateway.selectedPid = args.pid;
        try {
            return await this.applySteps(args, steps);
        } finally {
            this.gateway.operationDecision = null;
        }
    }

    async applySteps(args, steps) {
        const initial = await this.readState(args);
        const guard = this.shapeGuard(args, steps, initial);
        if (guard !== null) return guard;

        const entries = [];
        let last = initial;
        let rideOnNode = false;
        for (const [index, step] of steps.entries()) {
            const dispatchesBefore = this.gateway.possibleDispatches;
            try {
                if (step.action === 'wait') {
                    const waited = await this.waitForState(args, step);
                    if (waited === null) return this.waitNotMetReply(index, entries);

                    entries.push(waitEntry(step, last, waited, rideOnNode));
                    rideOnNode = false;
                    last = waited.reading;
                    continue;
                }

                const before = index === 0 ? initial : await this.readState(args);
                const refusal = this.stepRefusal(step, index, before, entries);
                if (refusal !== null) return refusal;

                const postcondition = postconditionFor(steps[index + 1], before);
                const after = await this.performAndRead(args, step, before, postcondition);
                entries.push(stepEntry(step, before, after));
                if (postcondition !== null) rideOnNode = true;
                last = after;
                const dialog = openedDialog(before, after);
                if (dialog === null) continue;

                const reply = index + 1 < steps.length
                    ? this.dialogOpenedReply(index, entries)
                    : this.successReply(args, entries, last, initial);
                return await this.dialogStopReply(args, dialog, reply);
            } catch (error) {
                return this.failureReply(error.message, index, entries, dispatchesBefore);
            }
        }

        return this.successReply(args, entries, last, initial);
    }

    // Unnamed fields are bound by position, so a step that names one needs the shape of the state it was read from.
    shapeGuard(args, steps, reading) {
        const positional = steps.some(step => reading.bindings.get(step.path)?.fallback === true);
        if (!positional) return null;
        if (args.shape === undefined) return this.shapeRefusal('shape_required', reading);
        if (args.shape !== reading.snapshot.shape) return this.shapeRefusal('shape_changed', reading);

        return null;
    }

    shapeRefusal(error, reading) {
        return { success: false, error, hint: shapeHint, shape: reading.snapshot.shape,
            declaration: reading.snapshot.declaration };
    }

    // A refusal names the step that failed; a later step also reports the steps that already ran.
    stepRefusal(step, index, reading, entries) {
        const detail = refusalFor(step, reading.bindings.get(step.path));
        if (detail === null) return null;

        const refusal = { success: false, ...detail, path: step.path, failed_step: index + 1,
            declaration: reading.snapshot.declaration };
        if (index === 0) return refusal;

        return { ...refusal, verified: false, completed_steps: entries.length, failed_step_may_have_changed_ui: false,
            steps: entries, retry: retryHint };
    }

    // A write is read back at the row it was bound to; a click has no value to confirm.
    async performAndRead(args, step, before, postcondition) {
        const binding = before.bindings.get(step.path);
        const node = before.nodes.find(candidate => candidate.id === binding.id);
        const identity = semanticTarget(args.pid, before.nodes, node, before.window);
        const input = step.action === 'set' ? { action: 'write', text: step.value } : { action: 'click' };
        await this.gateway.performIdentity(args.pid, input, identity, null, false, args.window, postcondition);

        const after = await this.readState(args);
        if (readsBack(step, binding) && valueAt(after, step.path) !== step.value) throw new Error('write_not_confirmed');

        return after;
    }

    // Every step reads the window again, so the bindings always describe the rows that the next input reaches.
    async readState(args) {
        const observation = await this.gateway.observeWindows(args.pid);
        const reading = await this.gateway.readTree(args.pid, false, args.window);
        if (!observation.unverified) await this.gateway.requireApplicationInstance(args.pid);
        const built = buildWindowState(reading.nodes, { windowHandle: reading.window.WindowHandle, kind: reading.window.Kind });
        if (built.error !== undefined) throw new Error('The window tree has no window row.');

        return { nodes: reading.nodes, window: reading.window, snapshot: built.value,
            bindings: bindWindowState(reading.nodes).value, windows: observation.windows };
    }

    failureReply(error, index, entries, dispatchesBefore) {
        return { success: false, error, verified: false, failed_step: index + 1, completed_steps: entries.length,
            failed_step_may_have_changed_ui: this.gateway.possibleDispatches > dispatchesBefore, steps: entries,
            retry: retryHint };
    }

    // A wait sends no input, so a wait that does not hold leaves the window as the previous step left it.
    waitNotMetReply(index, entries) {
        return { success: false, error: 'wait_not_met', hint: waitNotMetHint, verified: false, failed_step: index + 1,
            completed_steps: entries.length, failed_step_may_have_changed_ui: false, steps: entries, retry: retryHint };
    }

    // The request stops before the next step, so that next step is the one that did not run.
    dialogOpenedReply(index, entries) {
        return { success: false, error: 'dialog_opened', verified: false, failed_step: index + 2,
            completed_steps: entries.length, failed_step_may_have_changed_ui: false, steps: entries, retry: retryHint };
    }

    // Polls a fresh state until the awaited condition holds or the timeout ends.
    async waitForState(args, step) {
        const started = Date.now();
        let reading = await this.readState(args);
        while (!conditionHolds(step, reading)) {
            if (Date.now() - started >= step.timeoutMilliseconds) return null;

            await pause(waitPollMilliseconds);
            reading = await this.readState(args);
        }

        return { reading, waitedMilliseconds: Date.now() - started };
    }

    // The dialog becomes a window of its own, so the next call may select and change it.
    async dialogStopReply(args, dialogWindow, reply) {
        this.gateway.knownWindows.set(dialogWindow.WindowHandle, args.pid);
        const reading = await this.gateway.readTree(args.pid, false, dialogWindow.WindowHandle);
        const built = buildWindowState(reading.nodes, { windowHandle: reading.window.WindowHandle, kind: reading.window.Kind });
        if (built.error !== undefined) throw new Error('The window tree has no window row.');

        return { ...reply, dialog: built.value, hint: dialogOpenedHint };
    }

    successReply(args, entries, last, initial) {
        const reply = { success: true, verified: true, steps: entries, state: last.snapshot.state,
            shape: last.snapshot.shape, pid: args.pid, window: last.snapshot.window, kind: last.snapshot.kind,
            title: last.snapshot.title };
        if (last.snapshot.shape !== initial.snapshot.shape) reply.declaration = last.snapshot.declaration;
        if (last.snapshot.more !== undefined) reply.more = last.snapshot.more;

        return reply;
    }
}

// Returns the typed steps, or null when a step is not a set, a call or a valid wait, or the count is outside 1..12.
function parseSteps(steps) {
    if (!Array.isArray(steps) || steps.length < minimumSteps || steps.length > maximumSteps) return null;
    const parsed = steps.map(parseStep);

    return parsed.includes(null) ? null : parsed;
}

// Returns the typed step, or null when the step is neither an exact set, an exact call nor a valid wait.
function parseStep(step) {
    if (typeof step !== 'object' || step === null) return null;
    if (hasExactKeys(step, ['set', 'value']) && typeof step.set === 'string' && typeof step.value === 'string') {
        return { action: 'set', path: step.set, value: step.value };
    }
    if (hasExactKeys(step, ['call']) && typeof step.call === 'string') return { action: 'call', path: step.call };
    if (typeof step.wait === 'string') return parseWaitStep(step);

    return null;
}

// A wait names one condition; its value is required exactly for the value condition, and its timeout is 1..30000 ms.
function parseWaitStep(step) {
    if (Object.keys(step).some(key => !waitKeys.includes(key))) return null;
    if (!waitConditions.has(step.until) || !valueFitsCondition(step)) return null;
    const timeout = step.timeout_ms === undefined ? defaultWaitMilliseconds : step.timeout_ms;
    if (!Number.isInteger(timeout) || timeout < minimumWaitMilliseconds || timeout > maximumWaitMilliseconds) return null;

    return { action: 'wait', path: step.wait, until: step.until, value: step.value, timeoutMilliseconds: timeout };
}

// A value is required exactly for the value condition; a value on any other condition is refused.
function valueFitsCondition(step) {
    if (step.until === 'value') return typeof step.value === 'string';

    return step.value === undefined;
}

// Each condition is judged on one fresh state; a path without a binding has no row, so only hidden and absent hold.
function conditionHolds(step, reading) {
    const binding = reading.bindings.get(step.path);
    if (binding === undefined) return absentConditions.has(step.until);
    if (step.until === 'enabled') return binding.enabled === true;
    if (step.until === 'disabled') return binding.enabled === false;
    if (step.until === 'value') return valueAt(reading, step.path) === step.value;

    return step.until === 'exists' || step.until === 'visible';
}

// Returns the dialog window that the step listed after it and not before, or null when the step opened none.
function openedDialog(before, after) {
    const handlesBefore = before.windows.map(window => window.WindowHandle);

    return after.windows.find(window => window.Kind === 'dialog' && !handlesBefore.includes(window.WindowHandle)) ?? null;
}

// A wait reports the change since the previous state of this request, and the time that it waited.
function waitEntry(step, previous, waited, rodeOnNode) {
    const window = { windowHandle: previous.window.WindowHandle, kind: previous.window.Kind };
    const changed = diffWindowStates(previous.nodes, waited.reading.nodes, window).value;

    const entry = { wait: step.path, until: step.until, waited_ms: waited.waitedMilliseconds, changed };
    if (rodeOnNode) entry.postcondition = 'node';

    return entry;
}

// A wait right after an action on a named member becomes the node's postcondition of that action, so the node also stops
// for an unexpected dialog and for the person at the machine. Positional or not yet present members keep polling.
function postconditionFor(following, before) {
    if (following === undefined || following.action !== 'wait') return null;
    const binding = before.bindings.get(following.path);
    if (binding === undefined || binding.fallback === true) return null;
    const row = before.nodes.find(candidate => candidate.id === binding.id);
    if (row === undefined) return null;

    const postcondition = { name: row.name, role: row.role };
    if (row.autoId) postcondition.auto_id = row.autoId;
    postcondition.expected_state = following.until === 'value' ? 'exists' : following.until;
    if (following.until === 'value') postcondition.expected_text = following.value;
    postcondition.timeout_ms = following.timeoutMilliseconds;

    return postcondition;
}

function pause(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function hasExactKeys(object, names) {
    const keys = Object.keys(object);

    return keys.length === names.length && names.every(name => keys.includes(name));
}

// Returns the refusal detail for a step, or null when the bound member takes the step.
function refusalFor(step, binding) {
    if (binding === undefined) return { error: 'unknown_path' };
    if (step.action === 'set' && !settableKinds.has(binding.member)) return { error: 'not_settable', hint: settableHint };
    if (step.action === 'call' && !callableKinds.has(binding.member)) return { error: 'not_callable', hint: callableHint };

    return null;
}

function readsBack(step, binding) {
    return step.action === 'set' && readBackKinds.has(binding.member);
}

// The value of the row that the path is bound to now; undefined when the path no longer exists.
function valueAt(reading, path) {
    const binding = reading.bindings.get(path);
    if (binding === undefined) return undefined;
    const node = reading.nodes.find(candidate => candidate.id === binding.id);
    if (node === undefined) return undefined;

    return node.value ?? '';
}

// A write that the node does not read back is marked, so nobody takes its missing value for a confirmed one.
function stepEntry(step, before, after) {
    const binding = before.bindings.get(step.path);
    const window = { windowHandle: before.window.WindowHandle, kind: before.window.Kind };
    const changed = diffWindowStates(before.nodes, after.nodes, window).value;
    const entry = { [step.action]: step.path, changed };
    if (step.action === 'set' && !readsBack(step, binding)) entry.write_only = true;

    return entry;
}
