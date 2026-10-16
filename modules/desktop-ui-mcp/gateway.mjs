import { UiProjection, parseNativeTree, observedTarget, presentTarget, hasWindowAncestor } from './projection.mjs';
import { buildWindowState } from './window-state.mjs';
import { validateUiArguments } from './protocol.mjs';
import crypto from 'node:crypto';
import { LearnedFunctions, validateLearning, readOutput, readOptionalOutput, functionInputSchema, validateBrowse,
    selectProcessHint, severalApplicationsRefusal, summarizeListedFunctions, noStoredFunctionsSentence, StoredFunctionDiscovery,
    storedFunctionsSentenceFor, stepLabel } from './learned-functions.mjs';
import { captureFunctions, announcedCaptures } from './automatic-capture.mjs';
import { FormScanner, validateScan } from './form-scanner.mjs';
import { ResultStore, resultDefinition } from './result-store.mjs';
import { formatResult, validateResultOptions } from './result-format.mjs';
import { FormRuntime } from './form-runtime.mjs';
import { formDefinitions } from './form-contract.mjs';
import { WorkflowRuntime, workflowDefinitions } from './workflow-runtime.mjs';
import { CompatibilityRuntime, compatibilityDefinitions } from './compatibility-runtime.mjs';
import { isPlainTextEntry } from './role-vocabulary.mjs';
import { viewLimits } from './view-store.mjs';
import { WindowApply } from './window-apply.mjs';

const checkpointedMutationTools = new Set(['click_element', 'type_text', 'focus_window', 'hotkey', 'set_clipboard']);
// The same words as the compact gateway, so both implementations answer each case with one message and one usage hint.
export const pidRefusal = 'Select a PID from the current ui_info reply before observing or operating it.';
// The same words as the compact gateway; a key name with a plus sign is refused and this sentence names the separate keys.
export const keyNameHint = ' Name each key separately, for example ["ctrl", "shift", "s"].';
// A refusal without closing punctuation gets a period, so the separate-key hint starts its own sentence.
function sentenceEnd(message) {
    return message.length === 0 || /[.!?]$/.test(message) ? '' : '.';
}
export const usageHint = 'Run a listed function with cartridge_call {function, arguments: {pid, parameters: {name: value}}}; '
    + 'a reply with verified true is final.';
export const stateHint = 'Read a window as a typed class with ui_observe {pid, window, as: "state"}, then change it with ui_apply.';
export const applyHint = 'Change it with ui_apply {pid, shape, steps: [{set: path, value}, {call: path}]}.';
export const stateCursorHint = 'Use a cursor from the more map of the latest state reply.';
export const compactInstructions = 'Author missing task procedures, then use them yourself through MCP. '
    + 'Identify the application/build; on version drift, test the needed behavior before adapting or deriving its cartridge. '
    + 'Explore with the available screenshot/UI controls, parameterize and verify the procedure, and reuse it with fresh inputs. '
    + 'Aim for one business-operation call; keep deterministic steps out of model context. '
    + 'Programming.learn captures supported ui_act demonstrations; other procedures need a loaded cartridge. '
    + 'Call ui_info first; each window row lists the stored functions it can run. '
    + 'Run a listed function with cartridge_call {function, arguments: {pid, parameters: {name: value}}}. '
    + 'When the function verifies the requested business result, do not observe it again.';
const windowRefusal = 'The window handle is not one of the current windows; call ui_info again.';
const unknownWindowRefusal = windowRefusal;
export const ambiguousWindowRefusal = 'Recipe window is absent, foreign or ambiguous; name the main window with the window argument '
    + '(the native handle from ui_info).';

// Mirrors the node's choice: a named handle must be current; otherwise the process's only window, else its only dialog.
export function chooseWindow(windows, pid, handle = undefined) {
    const ofProcess = windows.filter(window => window.ProcessId === pid);
    if (handle !== undefined) return ofProcess.find(window => window.WindowHandle === handle);
    if (ofProcess.length === 1) return ofProcess[0];
    const dialogs = ofProcess.filter(window => window.Kind === 'dialog');

    return dialogs.length === 1 ? dialogs[0] : undefined;
}

// The same words as the compact gateway; learning and reuse prove the window of their demonstration with these refusals.
export const demonstrationWindowRefusal = 'The window the demonstration used is no longer current; observe it again.';
export const uniqueWindowRefusal = 'Select one unambiguous current application window for learning/reuse.';
export const referenceWindowRefusal = 'The observed reference belongs to another window; select its window with the window argument.';

// Mirrors the compact gateway's learning rule. A named handle must be a current window with a known start; without a handle the
// windows of the process must be exactly one such window. The windows passed in belong to one process.
export function requireInstanceWindow(windows, handle = undefined) {
    if (handle !== undefined && handle !== null) {
        const named = windows.find(window => window.WindowHandle === handle);
        if (!named || !hasProcessStart(named)) throw new Error(demonstrationWindowRefusal);

        return named;
    }
    if (windows.length !== 1 || !hasProcessStart(windows[0])) throw new Error(uniqueWindowRefusal);

    return windows[0];
}

function hasProcessStart(window) {
    return typeof window.ProcessStartedAtUtc === 'string' && window.ProcessStartedAtUtc.length > 0;
}

// The native gateway refuses with these same two sentences, so both twins name each situation identically.
export const unnamedTargetRefusal = 'A control without a name or automation ID can only take a text write into a text field '
    + 'bound to its window; choose a named control.';
export const unnamedTextFieldRefusal = 'This unnamed text field has no native text interface; it cannot be written safely.';

// A control without a name or automation ID is identified only by its window and container path. Only a text write may reach one,
// because its read-back proves the outcome; a click or a choice has no such proof, so it stays refused.
export function unnamedWriteAllowed(target, action) {
    // A stored selector names its window kind as `window`; an observed target names it as `windowKind`.
    return action === 'write' && isPlainTextEntry(target.role) && Array.isArray(target.containerPath)
        && (Boolean(target.windowKind || target.window) || target.windowHandle !== undefined);
}

// An unnamed element with an empty container path must be a direct child of a window row in the tree that was read.
function requireProvenPath(nodes, target, id) {
    if (target.containerPath.length > 0) return;
    if (!hasWindowAncestor(nodes, nodes.find(node => node.id === id))) throw new Error(unnamedTargetRefusal);
}

// An unnamed field has no name that proves focus, so a clipboard paste could land in another control and would replace the person's
// clipboard. Only the native text interface may write it.
function requireNamedForKeyboard(target) {
    if (!target.autoId && !target.name) throw new Error(unnamedTextFieldRefusal);
}

// This UI topic executes native MCP primitives through the node, which continues enforcing snapshots, scopes and input policy.
// No browser DOM or program API is available here. Native calls can be audited separately from model-facing round trips.
export class DesktopUiGateway {
    constructor(client, audit = () => {}, learning = null, evidence = {}) {
        this.client = client;
        this.audit = audit;
        this.projection = new UiProjection(evidence);
        this.observedInstances = new Map();
        this.knownPids = new Set();
        this.knownWindows = new Map();
        this.selectedWindow = null;
        this.selectedPid = null;
        this.windowInstances = new Map();
        this.noTextInterface = new Set();
        this.serverInfo = null;
        this.platform = null;
        this.applicationWindows = [];
        this.lastNodes = [];
        this.listedRows = [];
        this.storedDiscovery = StoredFunctionDiscovery.notObserved;
        this.demonstration = null;
        this.learned = learning ? new LearnedFunctions(learning) : null;
        this.compatibilityRuntime = new CompatibilityRuntime(this, evidence.compatibility ?? learning?.compatibility ?? null);
        this.forms = this.learned ? new FormScanner(this.learned) : null;
        this.automaticCapture = Boolean(learning && learning.auto_capture !== false);
        this.results = new ResultStore(evidence.clock);
        this.resultNode = evidence.node ?? client;
        this.formRuntime = new FormRuntime(this, evidence.clock);
        this.operationRecorder = null;
        this.possibleDispatches = 0;
        this.operationDecision = null;
        this.workflowRuntime = evidence.workflow ? new WorkflowRuntime(this, evidence.workflow,
            learning => new DesktopUiGateway(client, audit, learning, { node: evidence.node, clock: evidence.clock })) : null;
        client.process?.once('exit', () => this.disconnect());
    }

    disconnect() {
        this.formRuntime.clear();
        this.learned?.discovery.clear();
        this.results.clear();
        this.demonstration = null;
        this.projection.clear();
        this.observedInstances.clear();
        this.knownPids.clear();
        this.knownWindows.clear();
        this.selectedWindow = null;
        this.selectedPid = null;
        this.lastNodes = [];
        this.noTextInterface.clear();
        this.windowInstances.clear();
        this.applicationWindows = [];
    }

    async call(name, args) {
        if (name.startsWith('cartridge_')) return this.cartridgeCall(name, args);
        validateUiArguments(name, args);
        if (name === 'ui_info') return this.info();
        const selection = this.selectWindow(args);
        const selected = { ...args, pid: selection.pid, window: selection.window };
        if (name === 'ui_observe') return this.observe(selected);
        if (name === 'ui_apply') return new WindowApply(this).run(selected);
        return this.act(selected);
    }

    // Throws when a named window is not a current window of the requested process; a refused handle makes no node call.
    selectWindow(args) {
        if (args.window !== undefined) return this.namedWindow(args.window, args.pid);
        if (!Number.isInteger(args.pid) || args.pid < 1) throw new Error('Select an observed PID.');

        return this.rememberedWindow(args.pid);
    }

    namedWindow(window, pid) {
        const owner = this.knownWindows.get(window);
        if (owner === undefined || pid !== undefined && owner !== pid) throw new Error(windowRefusal);
        this.selectedWindow = window;

        return { pid: owner, window };
    }

    // A remembered window stays selected for a pid-only request only while Info still lists it under that process.
    rememberedWindow(pid) {
        if (!this.knownPids.has(pid)) throw new Error(pidRefusal);
        if (this.selectedWindow !== null && this.knownWindows.get(this.selectedWindow) !== pid) this.selectedWindow = null;

        return { pid, window: this.selectedWindow ?? undefined };
    }

    // Generated functions observe the process of the named window; the window itself never changes what they read.
    functionArguments(input) {
        return { ...input, pid: this.selectWindow(input).pid };
    }

    async native(name, args = {}) {
        const checkpointed = this.operationRecorder && checkpointedMutationTools.has(name);
        if (this.operationDecision && ['click_element', 'type_text'].includes(name)) args = { ...args, decision: this.operationDecision };
        if (checkpointed) args = this.operationRecorder.before(name, args);
        // A mutation counts as possibly dispatched until its reply proves the node refused it before dispatch.
        const mutating = checkpointedMutationTools.has(name);
        if (mutating) this.possibleDispatches++;
        const response = await this.client.call(name, args);
        const result = response.result;
        let payload = result?.structuredContent;
        if (!payload) {
            try { payload = JSON.parse(result?.content?.find(part => part.type === 'text')?.text ?? '{}'); }
            catch { payload = {}; }
        }
        this.audit({ tool: name, arguments: args, resultBytes: Buffer.byteLength(JSON.stringify(result ?? response.error)),
            imageCount: result?.content?.filter(part => part.type === 'image').length ?? 0,
            success: !response.error && !result?.isError && payload.success === true,
            outcome: payload.outcome, error: payload.error ?? payload.message });
        if (checkpointed) this.operationRecorder.after(args.request_id,
            { ...payload, toolError: result?.isError === true, transport_error: response.error ?? null });
        if (response.error) throw new Error(response.error.message);
        if (mutating && payload.outcome === 'not_dispatched') this.possibleDispatches--;
        return { ...payload, toolError: result?.isError === true,
            images: result?.content?.filter(part => part.type === 'image') ?? [] };
    }

    requireSuccess(result) {
        if (result.success !== true || result.toolError) throw new Error(result.error ?? result.message ?? 'Node operation is unconfirmed.');
        return result;
    }

    // A chord written as one string is refused by the node; the refusal then names each key separately.
    requireHotkey(result, keys) {
        try {
            this.requireSuccess(result);
        } catch (error) {
            if (!keys.some(key => key.includes('+'))) throw error;
            throw new Error(error.message + sentenceEnd(error.message) + keyNameHint);
        }
    }

    async info() {
        const capabilities = this.requireSuccess(await this.native('get_machine_capabilities'));
        const result = this.requireSuccess(await this.native('list_windows'));
        const windows = result.windows.map(window => ({ pid: window.ProcessId, title: window.Title,
            version: window.AppVersion ?? 'unknown', process_name: window.ProcessName ?? 'unknown',
            window: window.WindowHandle, kind: window.Kind }));
        this.applicationWindows = windows;
        this.platform = capabilities.capabilities?.platform ?? 'unknown';
        this.knownPids = new Set(windows.map(window => window.pid));
        this.knownWindows = new Map(windows.map(window => [window.window, window.pid]));
        this.windowInstances = new Map(result.windows.map(window => [window.WindowHandle,
            { pid: window.ProcessId, started: window.ProcessStartedAtUtc }]));
        if (this.learned) await this.compatibilityRuntime.observe(capabilities.capabilities ?? {});
        // Resolution keeps every runnable name, generated ones included; only the rows the model reads are summarized.
        this.listedRows = windows.map(window => ({ ...window, runnable: this.runnableStoredFunctions(window) }));
        const listed = this.listedRows.map(row => this.listedWindow(row));
        const namedFunctionListed = listed.some(window => window.functions !== undefined);
        this.storedDiscovery = namedFunctionListed ? StoredFunctionDiscovery.listed : StoredFunctionDiscovery.noneListed;
        const next = namedFunctionListed ? usageHint + ' ' + stateHint : stateHint;
        return { success: true, node: this.serverInfo, platform: this.platform,
            compatibility: { tested_node: '0.2610.0609', tested_platform: 'linux-x11-atspi', native_depth_limit: 32,
                other_platforms: 'not validated; observation must parse successfully before references are issued' },
            routes: ['native UI actions', 'semantic focus and keyboard', 'optional screenshots'],
            form_capabilities: capabilities.capabilities?.formCapabilities ?? 'unsupported', windows: listed, next };
    }

    // A row names only the stored Use functions that cartridge_call would run for its program now, each with its parameter names.
    listedWindow(row) {
        const { runnable, ...window } = row;
        const functions = runnable.map(entry => ({ name: entry.name, description: entry.description,
            parameters: declaredParameters(entry), steps: entry.steps.map(stepLabel) }));
        const summary = summarizeListedFunctions(functions);
        if (summary.functions.length === 0) return window;

        return { ...window, functions: summary.functions, ...(summary.more > 0 ? { more_functions: summary.more } : {}) };
    }

    // An unknown program name or version is never matched, because the reply could not say which program the row is.
    runnableStoredFunctions(window) {
        if (!this.learned || window.process_name === 'unknown' || window.version === 'unknown') return [];
        if (!this.ownsStoredFunctions(window)) return [];

        return this.learned.functions.filter(entry => entry.topic.startsWith('Use/')
            && this.compatibilityRuntime.assess(entry).status === 'reusable');
    }

    // Stored functions belong to the configured program; a native-version program must match the row's name and version.
    ownsStoredFunctions(window) {
        const application = this.learned.application;
        if (application.identity_source !== 'native-version') return true;

        return window.process_name === application.process_name && window.version === application.version;
    }

    // Browsing offers stored functions only through the selected process, the same rule the compact gateway applies.
    storedFunctionsSelected() {
        if (!this.learned || this.selectedPid === null) return false;
        const row = this.applicationWindows.find(window => window.pid === this.selectedPid);

        return row !== undefined && this.ownsStoredFunctions(row);
    }

    async observe(args) {
        if (args.image) {
            const result = this.requireSuccess(await this.native('take_screenshot'));
            return { success: true, images: result.images, width: result.width, height: result.height,
                view_ref: crypto.randomUUID(), kind: 'full', next_cursor: null, baseline_reason: 'image_observation' };
        }
        if (args.as === 'state') return this.observeState(args);
        const observation = await this.observeWindows(args.pid);
        const result = await this.readTree(args.pid, false, this.selectWindow(args).window);
        if (!observation.unverified) await this.requireApplicationInstance(args.pid);
        // A full view lists offscreen controls only when asked for them or searched by contains, as the compact C# gateway does.
        const offscreen = args.offscreen === true || Boolean(args.contains);
        const projection = this.projection.observe(args.pid, result.nodes, { ...args, offscreen, instance: observation.instance,
            window: result.window });
        if (observation.unverified) projection.baseline_reason = 'instance_unverified';
        this.selectedPid = args.pid;
        return { success: true, ...projection };
    }

    // The state route reads the same window as the elements view, but answers with the window as a typed class.
    async observeState(args) {
        const observation = await this.observeWindows(args.pid);
        const result = await this.readTree(args.pid, false, this.selectWindow(args).window);
        if (!observation.unverified) await this.requireApplicationInstance(args.pid);
        const reply = buildWindowState(result.nodes, { windowHandle: result.window.WindowHandle, kind: result.window.Kind,
            maxChars: args.max_chars ?? 2000, cursor: args.cursor ?? null });
        if (reply.error === 'no_window') throw new Error('The window tree has no window row.');
        if (reply.error === 'unknown_part') return { success: false, error: 'unknown_part', hint: stateCursorHint };
        this.selectedPid = args.pid;
        return { ...reply.value, success: true, pid: args.pid, next: applyHint };
    }

    async observeWindows(pid) {
        const result = this.requireSuccess(await this.native('list_windows'));
        const windows = result.windows.filter(window => window.ProcessId === pid);
        if (!windows.length) throw new Error('Observed application window is absent. Refresh Info.');
        const proved = windows.every(window => window.WindowHandle && typeof window.ProcessStartedAtUtc === 'string');
        const instance = proved ? JSON.stringify(windows.map(window => [window.WindowHandle, window.ProcessStartedAtUtc]).sort())
            : 'unverified-' + crypto.randomUUID();
        if (this.observedInstances.has(pid) && this.observedInstances.get(pid) !== instance) this.projection.resetApplication(pid);
        this.observedInstances.set(pid, instance);
        for (const [handle, window] of this.windowInstances) if (window.pid === pid) this.windowInstances.delete(handle);
        for (const window of windows) this.windowInstances.set(window.WindowHandle, { pid, started: window.ProcessStartedAtUtc });
        this.applicationWindows = [...this.applicationWindows.filter(window => window.pid !== pid), ...windows.map(window =>
            ({ pid, title: window.Title, version: window.AppVersion ?? 'unknown', process_name: window.ProcessName ?? 'unknown' }))];
        return { windows, instance, unverified: !proved };
    }

    async resolve(pid, ref) {
        const target = this.projection.target(ref, pid);
        return this.resolveIdentity(pid, target);
    }

    // A tree is always read for one native window, so every row can be attributed to that window's kind.
    async readTree(pid, fieldMetadata = false, window = undefined) {
        const observed = await this.observedWindow(pid, window);
        const result = this.requireSuccess(await this.native('get_ui_tree', { pid, window: observed.WindowHandle, max_depth: 32,
            max_nodes: 2000, ...(fieldMetadata ? { field_metadata: true } : {}) }));
        if (result.truncated) throw new Error('The native tree is incomplete; completion is unverified.');
        this.lastNodes = parseNativeTree(result.tree);
        if (fieldMetadata) {
            for (const node of this.lastNodes) {
                const choices = result.form_metadata?.[node.id]?.Choices;
                const selected = choices?.Options?.find(option => option.Id === choices.SelectedId);
                if (selected) node.value = selected.Label;
            }
        }
        return { nodes: this.lastNodes, snapshot_id: result.snapshot_id, metadata: result.form_metadata ?? {}, window: observed };
    }

    // Throws when no single current window is proved, so no tree is read for a guessed window. Entry points pass the window that
    // selectWindow names (a named, remembered or absent window); internal reads pass the handle they already bound.
    async observedWindow(pid, window = undefined) {
        const result = this.requireSuccess(await this.native('list_windows'));
        const observed = chooseWindow(result.windows, pid, window);
        if (!observed) throw new Error(window === undefined ? ambiguousWindowRefusal : unknownWindowRefusal);
        return observed;
    }

    async resolveIdentity(pid, target, stableIdentity = false, window = undefined, action = undefined) {
        const unnamed = !target.autoId && !target.name;
        if (unnamed && !unnamedWriteAllowed(target, action)) throw new Error(unnamedTargetRefusal);
        if (target.windowHandle !== undefined) target = await this.currentWindowTarget(pid, target);
        const result = await this.readTree(pid, false, target.windowHandle ?? window);
        const id = observedTarget(result.nodes, target, stableIdentity);
        if (unnamed) requireProvenPath(result.nodes, target, id);
        return { id, snapshot_id: result.snapshot_id };
    }

    async currentWindowTarget(pid, target) {
        const result = this.requireSuccess(await this.native('list_windows'));
        const matches = result.windows.filter(window => window.ProcessId === pid && window.WindowHandle === target.windowHandle
            && typeof target.processStarted === 'string' && window.ProcessStartedAtUtc === target.processStarted);
        if (matches.length !== 1) throw new Error('The observed window instance was replaced or cannot be verified.');
        target.windowKind = matches[0].Kind;
        return target;
    }

    async act(args) {
        this.operationDecision = args.decision ?? null;
        this.selectedPid = args.pid;
        let completed = 0;
        const routes = [];
        const visibleReferences = new Map(this.projection.references);
        const possibleDispatchesBeforeAct = this.possibleDispatches;
        let possibleDispatchesBeforeStep = possibleDispatchesBeforeAct;
        let phase = 'execution';
        this.demonstration = null;
        try {
            await this.requireApplicationInstance(args.pid);
            const observedBefore = this.learned ? await this.readTree(args.pid, false, this.selectWindow(args).window) : null;
            const before = observedBefore?.nodes ?? null;
            const bindings = args.steps.map(step => step.ref ? this.projection.target(step.ref, args.pid) : null);
            for (const [index, step] of args.steps.entries()) {
                possibleDispatchesBeforeStep = this.possibleDispatches;
                routes.push(await this.perform(args.pid, step, args.window));
                if (step.action === 'write') {
                    phase = 'verification';
                    await this.verifyWrittenIdentity(args.pid, bindings[index], step.text, args.window);
                    phase = 'execution';
                }
                completed++;
            }
            phase = 'after_steps';
            const ui = await this.observe(args);
            const result = { success: true, completed_steps: completed, routes, ui };
            const checks = args.result_mode !== undefined ? this.verifyActionWrites(result, args.steps, bindings) : {};
            if (this.learned) {
                this.demonstration = { evidence: crypto.randomUUID(), pid: args.pid, steps: structuredClone(args.steps),
                    bindings, routes, before, after: this.lastNodes, windowHandle: observedBefore.window.WindowHandle };
                result.evidence = this.demonstration.evidence;
                if (this.automaticCapture) result.mcp = announcedCaptures(await captureFunctions(this, args.result_mode !== undefined));
            }
            return this.finishAction(result, args, visibleReferences, checks);
        } catch (error) {
            // Only the failing step's own native calls count; after the last step, every call of this action counts.
            const afterAllSteps = completed === args.steps.length;
            const baseline = afterAllSteps ? possibleDispatchesBeforeAct : possibleDispatchesBeforeStep;
            const result = { success: false, completed_steps: completed,
                failed_step_may_have_changed_ui: this.possibleDispatches > baseline, verified: false, error: error.message,
                retry: 'Do not automatically retry any mutation or replay completed steps; observe the UI and inspect evidence.' };
            // After the last step there is no failing step to index; the phase names where the failure happened instead.
            if (afterAllSteps) result.phase = 'after_steps';
            else result.failed_step = completed;
            if (args.result_mode !== undefined) Object.assign(result, { phase, observation_required: true });
            return this.finishAction(result, args, visibleReferences, {});
        }
    }

    verifyActionWrites(result, steps, bindings) {
        const outputs = {};
        const checks = {};
        for (const [index, step] of steps.entries()) {
            if (step.action !== 'write') continue;
            if (steps.slice(index + 1).some((later, offset) => later.action === 'write'
                && JSON.stringify(bindings[index]) === JSON.stringify(bindings[index + offset + 1]))) continue;
            const value = this.requireWrittenValue(this.lastNodes, selectorOf(bindings[index]), step.text);
            const name = 'step_' + (index + 1);
            outputs[name] = value;
            checks[name] = { verified: true, matches_input: true };
        }
        result.outputs = outputs;
        result.unconfirmed_effects = steps.flatMap((step, index) => step.action === 'write' ? [] : [{ step: index + 1, action: step.action }]);
        return checks;
    }

    requireWrittenValue(nodes, selector, text) {
        const value = readOutput(nodes, selector, 'value');
        if (value !== text) throw new Error('The observed value does not match the written input.');
        return value;
    }

    finishAction(result, args, visibleReferences, checks) {
        this.operationDecision = null;
        const formatted = formatResult(this.results, this.resultNode, result, args, checks);
        if (args.result_mode !== undefined && !formatted.ui) {
            this.projection.hideUnseen(visibleReferences, args.pid);
        }
        return formatted;
    }

    async requireApplicationInstance(pid) {
        const result = this.requireSuccess(await this.native('list_windows'));
        const sameInstance = result.windows.some(window => window.ProcessId === pid
            && this.windowInstances.get(window.WindowHandle)?.pid === pid
            && typeof window.ProcessStartedAtUtc === 'string'
            && this.windowInstances.get(window.WindowHandle)?.started === window.ProcessStartedAtUtc);
        if (!sameInstance) throw new Error('The observed application window was replaced. Refresh Info and observe before input.');
        this.applicationWindows = result.windows.map(window => ({ pid: window.ProcessId, title: window.Title,
            version: window.AppVersion ?? 'unknown', process_name: window.ProcessName ?? 'unknown' }));
    }

    async perform(pid, step, window = undefined) {
        if (step.action === 'key') {
            this.requireSuccess(await this.native('focus_window', { pid }));
            if (step.ref !== undefined) await this.focusKeyTarget(pid, step, window);
            this.requireHotkey(await this.native('hotkey', { keys: step.keys }), step.keys);
            return 'keyboard';
        }
        const identity = this.projection.target(step.ref, pid);
        return this.performIdentity(pid, step, identity, null, false, window);
    }

    // Moves keyboard focus to the referenced element without activating it, so the following keys reach that element.
    async focusKeyTarget(pid, step, window) {
        const identity = this.projection.target(step.ref, pid);
        const target = await this.resolveIdentity(pid, identity, false, window, 'click');
        this.requireSuccess(await this.native('click_element', { ...target, focus_only: true }));
    }

    // A postcondition rides on the native click or text request so the node waits for it; the keyboard route carries none.
    async performIdentity(pid, step, identity, knownRoute = null, stableIdentity = false, window = undefined,
        postcondition = null) {
        if (step.action === 'read') return 'native-read';
        if (step.action === 'select') {
            if (!identity.autoId && !identity.name) throw new Error(unnamedTargetRefusal);
            return this.performChoice(pid, step, identity, stableIdentity, window);
        }
        const keyboardRoute = knownRoute?.startsWith('semantic-focus+keyboard') === true;
        if (keyboardRoute) requireNamedForKeyboard(identity);
        const target = await this.resolveIdentity(pid, identity, stableIdentity, window, step.action);
        if (step.action === 'click') {
            this.requireSuccess(await this.native('click_element', withPostcondition(target, postcondition)));
            return 'semantic';
        }
        const key = JSON.stringify({ pid, ...identity });
        if (keyboardRoute) this.noTextInterface.add(key);
        if (!this.noTextInterface.has(key)) {
            const result = await this.native('type_text',
                withPostcondition({ ...target, text: step.text, clear: true }, postcondition));
            if (result.success === true && !result.toolError) return 'native-text';
            if (result.error || result.code || result.message !== 'Target has no editable text interface.') this.requireSuccess(result);
            this.noTextInterface.add(key);
        }
        if (knownRoute !== null && !keyboardRoute) {
            throw new Error('The recorded native text route is unavailable; revalidate before keyboard input.');
        }
        requireNamedForKeyboard(identity);
        this.requireSuccess(await this.native('click_element', target));
        this.requireSuccess(await this.native('set_clipboard', { text: step.text }));
        this.requireSuccess(await this.native('hotkey', { keys: ['ctrl', 'a'] }));
        this.requireSuccess(await this.native('hotkey', { keys: ['ctrl', 'v'] }));
        return 'semantic-focus+keyboard (native editable interface unavailable)';
    }

    async verifyWrittenIdentity(pid, identity, text, window = undefined) {
        await this.requireApplicationInstance(pid);
        if (identity.windowHandle !== undefined) identity = await this.currentWindowTarget(pid, identity);
        const observed = await this.readTree(pid, false, window);
        await this.requireApplicationInstance(pid);
        this.requireWrittenValue(observed.nodes, selectorOf(identity), text);
    }

    // A learned function's stored selector already names its window kind, so it is checked without a target.
    async verifyWrittenSelector(pid, selector, text, window = undefined) {
        await this.requireApplicationInstance(pid);
        const observed = await this.readTree(pid, false, window);
        await this.requireApplicationInstance(pid);
        this.requireWrittenValue(observed.nodes, selector, text);
    }

    async performChoice(pid, step, identity, stableIdentity, window = undefined) {
        const result = await this.readTree(pid, true, window);
        const id = observedTarget(result.nodes, identity, stableIdentity);
        const metadata = result.metadata[id];
        const matches = metadata?.Choices?.Options?.filter(option => option.Label === step.text && option.Enabled) ?? [];
        if (metadata?.Support !== 'Available' || metadata.Visible !== true || metadata.Enabled !== true || metadata.ReadOnly === true
            || !metadata.Choices.Complete || matches.length !== 1) throw new Error('Current native option is unavailable or ambiguous.');
        this.requireSuccess(await this.native('click_element', { id, snapshot_id: result.snapshot_id, choice_action: 'select',
            option_id: matches[0].Id, options_revision: metadata.Choices.Revision,
            ...(metadata.Context != null ? { field_context: metadata.Context } : {}) }));
        return 'native-choice';
    }

    async cartridgeCall(name, args) {
        if (name === 'cartridge_browse') validateBrowse(args);
        const compatibility = compatibilityDefinitions.find(entry => entry.name === args.function);
        if (compatibility && name === 'cartridge_browse') return { success: true, function: compatibility };
        if (compatibility && name === 'cartridge_call') {
            if (!this.learned) throw new Error('Select an application cartridge before compatibility assessment.');
            if (args.function === 'Info.compatibility') return this.compatibilityRuntime.info(args.arguments ?? {});
            return this.compatibilityRuntime.revalidate(args.arguments ?? {});
        }
        if (workflowDefinitions.some(definition => definition.name === args.function)
            || name === 'cartridge_browse' && ['Use/Workflows', 'Programming/Workflows'].includes(args.topic)) {
            if (!this.workflowRuntime) throw new Error('Private workflow configuration is not available.');
            return name === 'cartridge_browse' ? this.workflowRuntime.browse(args) : this.workflowRuntime.call(args.function, args.arguments ?? {});
        }
        if (name === 'cartridge_browse' && formDefinitions.some(entry => entry.name === args.function)) {
            return { success: true, function: formDefinitions.find(entry => entry.name === args.function) };
        }
        if (name === 'cartridge_call' && formDefinitions.some(entry => entry.name === args.function)) {
            return this.formRuntime.call(args.function, args.arguments ?? {});
        }
        if (!this.learned && name === 'cartridge_browse' && !args.function && ['Use', 'Use/Forms'].includes(args.topic)) {
            return { success: true, topics: ['Use/Forms'], functions: formDefinitions.map(({ name, topic }) => ({ name, topic })), next_cursor: null };
        }
        if (name === 'cartridge_browse' && args.function === 'Info.result') return { success: true, function: resultDefinition };
        if (name === 'cartridge_call' && args.function === 'Info.result') {
            return { success: true, ...this.results.read(this.resultNode, args.arguments ?? {}) };
        }
        if (!this.learned && name === 'cartridge_browse' && !args.function && (!args.topic || args.topic === 'Info')) {
            const storedFunctions = storedFunctionsSentenceFor(this.storedDiscovery);
            if (!args.topic) return { success: true, topics: ['Use', 'Info'], stored_functions: storedFunctions };
            return { success: true, topics: ['Info'], functions: [{ name: 'Info.result', topic: 'Info' }] };
        }
        if (!this.learned) throw new Error('No application/version is configured for learning.');
        if (name === 'cartridge_info') {
            const info = await this.info();
            return { success: true, ...this.learned.info(), observed_forms: Object.keys(this.forms.forms).length,
                node: this.serverInfo, platform: this.platform, windows: info.windows,
                stored_functions: storedFunctionsSentenceFor(this.storedDiscovery) };
        }
        if (name === 'cartridge_browse') return this.browseLearned(args);
        if (name !== 'cartridge_call' || typeof args.function !== 'string') throw new Error('Unknown cartridge tool.');
        if (args.function.startsWith('form.')) return this.formRuntime.call(args.function, args.arguments ?? {});
        if (args.function === 'Programming.learn') return this.learnFunction(args.arguments ?? {});
        if (args.function === 'Programming.learn_read') return this.learnRead(args.arguments ?? {});
        if (args.function === 'Info.preflight') return this.inspectFunction(args.arguments ?? {});
        if (args.function === 'Info.scan_form') return this.scanForm(args.arguments ?? {});
        if (args.function === 'Info.form') return { success: true, ...this.forms.read(args.arguments ?? {}) };
        return this.executeFunction(this.learned.get(args.function), this.functionArguments(args.arguments ?? {}));
    }

    // A window names its process and selects it, so its stored functions can be listed without a separate observation.
    browseLearned(args) {
        if (args.window !== undefined) this.selectedPid = this.selectWindow({ window: args.window }).pid;
        else if (args.pid !== undefined) this.selectedPid = this.selectWindow({ pid: args.pid }).pid;
        else if (this.selectedPid === null && args.function !== undefined) {
            const programs = this.listingPrograms(args.function);
            if (programs.length > 1) throw new Error(severalApplicationsRefusal);
            if (programs.length === 1) return { success: true, ...this.learned.browse(args, true) };
        }
        const storedVisible = this.storedFunctionsSelected();
        const reply = { success: true, ...this.learned.browse(args, storedVisible),
            ...(args.topic === 'Info' ? { observed_forms: Object.keys(this.forms.forms).slice(0, 50) } : {}),
            ...(!args.topic && args.function === undefined
                ? { stored_functions: storedFunctionsSentenceFor(this.storedDiscovery) } : {}) };
        // Built-in form tools may still be listed; the hint says that none of the entries is a stored function.
        const noneStored = this.storedDiscovery === StoredFunctionDiscovery.noneListed;
        if (isUseTopic(args.topic) && noneStored) reply.hint = noStoredFunctionsSentence;
        else if (this.selectedPid === null && isUseTopic(args.topic)) reply.hint = selectProcessHint;

        return reply;
    }

    // Processes of one program share its identity (process name and version), so they name one program that lists the function.
    listingPrograms(name) {
        const rows = this.listedRows.filter(row => row.runnable.some(entry => entry.name === name));

        return [...new Set(rows.map(row => JSON.stringify([row.process_name, row.version])))];
    }

    async scanForm(args) {
        validateScan(args);
        this.checkLearningIdentity(args.pid);
        await this.requireApplicationInstance(args.pid);
        const tree = await this.readTree(args.pid, false, this.selectWindow(args).window);
        this.selectedPid = args.pid;
        const compatibility = { node_version: this.serverInfo.version, platform: this.platform,
            application_version: this.learned.application.version };
        const result = { success: true, ...this.forms.scan(tree.nodes, args, compatibility, tree.window) };
        if (args.result_mode === 'values') {
            result.outputs = Object.fromEntries(result.contents.fields.map(field => [field.key, field.value ?? field.name]));
        }
        return formatResult(this.results, this.resultNode, result, args, {},
            { field_count: result.fields.length, content_count: result.contents?.fields.length ?? 0 });
    }

    async learnFunction(args) {
        validateLearning(args, this.demonstration);
        const demonstration = this.demonstration;
        this.checkLearningIdentity(demonstration.pid);
        await this.requireWindowInstance(demonstration.pid, demonstration.windowHandle);
        let parameterIndex = 0;
        const steps = demonstration.steps.map((step, index) => ({ action: step.action,
            selector: selectorOf(demonstration.bindings[index]), route: demonstration.routes[index],
            ...(step.action === 'write' ? { parameter: args.parameters[parameterIndex++] } : {}),
            ...(mayWaitForEarlierMutation(demonstration, index) && disabledBeforeFirstAction(demonstration, index)
                ? { enabledBeforeFirstAction: false } : {}) }));
        const outputs = args.outputs.map(output => this.compileOutput(output, steps));
        const properties = Object.fromEntries(args.parameters.map(parameter => [parameter, { type: 'string', maxLength: 16000 }]));
        const entry = { name: args.name, topic: args.topic, steps, outputs,
            ...(args.description !== undefined ? { description: args.description } : {}),
            compatibility: { node_version: this.serverInfo.version, platform: this.platform, application_version: this.learned.application.version },
            validation: 'demonstrated_once; every execution verifies outputs',
            inputSchema: { type: 'object', properties: { pid: { type: 'integer', minimum: 1 }, parameters: {
                type: 'object', properties, required: args.parameters, additionalProperties: false } },
            required: args.parameters.length > 0 ? ['pid', 'parameters'] : ['pid'], additionalProperties: false } };
        entry.compatibility = this.compatibilityRuntime.capture(entry);
        this.learned.install(entry);
        this.workflowRuntime?.recordCaptured(entry, this.learned.application);
        this.demonstration = null;
        return { success: true, function: entry.name, topic: entry.topic, inputSchema: functionInputSchema(entry),
            validation: entry.validation, stored_inputs: 'parameters only; no training values or screenshots' };
    }

    async learnRead(args) {
        const required = ['pid', 'name', 'topic', 'ref', 'field', 'output'];
        const allowed = [...required, 'window'];
        if (required.some(key => !Object.hasOwn(args, key)) || Object.keys(args).some(key => !allowed.includes(key))
            || !Number.isInteger(args.pid) || args.pid < 1 || typeof args.ref !== 'string'
            || args.window !== undefined && !Number.isInteger(args.window)
            || typeof args.name !== 'string' || typeof args.output !== 'string' || typeof args.topic !== 'string'
            || !/^[a-z][a-z0-9._-]{0,79}$/.test(args.name)
            || !/^[a-z][a-z0-9._-]{0,79}$/.test(args.output) || !/^(Use|Info)\/[A-Za-z0-9._/-]{1,100}$/.test(args.topic)
            || !['name', 'value'].includes(args.field)) throw new Error('Invalid observed read capture.');
        this.checkLearningIdentity(args.pid);
        const selection = this.selectWindow(args);
        const target = this.projection.target(args.ref, args.pid);
        const window = await this.requireWindowInstance(args.pid, selection.window);
        if (target.windowHandle !== window.WindowHandle) throw new Error(referenceWindowRefusal);
        const selector = selectorOf(target);
        const nodes = (await this.readTree(args.pid, false, window.WindowHandle)).nodes;
        if (!readOutput(nodes, selector, args.field)?.trim()) throw new Error('A nonempty observed source is required.');
        const entry = { name: args.name, topic: args.topic, steps: [{ action: 'read', selector, route: 'native-read' }],
            outputs: [{ name: args.output, selector, field: args.field, check: { kind: 'not_empty' } }],
            compatibility: { node_version: this.serverInfo.version, platform: this.platform, application_version: this.learned.application.version },
            validation: 'demonstrated_once; every execution verifies the current source',
            inputSchema: { type: 'object', properties: { pid: { type: 'integer', minimum: 1 },
                parameters: { type: 'object', properties: {}, required: [], additionalProperties: false } },
                required: ['pid'], additionalProperties: false } };
        entry.compatibility = this.compatibilityRuntime.capture(entry);
        this.learned.install(entry);
        this.workflowRuntime?.recordCaptured(entry, this.learned.application);
        return { success: true, function: entry.name, topic: entry.topic, inputSchema: functionInputSchema(entry), stored_values: false };
    }

    compileOutput(output, steps) {
        const target = this.projection.target(output.ref, this.demonstration.pid);
        const selector = selectorOf(target);
        if (output.name_prefix) {
            if (!target.name.startsWith(output.name_prefix)) throw new Error('Result prefix does not match the observed output.');
            delete selector.name;
            selector.namePrefix = output.name_prefix;
        }
        const after = readOutput(this.demonstration.after, selector, output.field);
        const earlier = readOptionalOutput(this.demonstration.before, selector, output.field);
        const existed = earlier.exists;
        const before = earlier.value;
        if (after === before) throw new Error('The output did not change; dispatched input alone cannot validate a function.');
        const write = steps.find(step => step.action === 'write' && JSON.stringify(step.selector) === JSON.stringify(selector));
        const parameter = write && output.field === 'value' ? write.parameter : null;
        if (parameter) {
            const original = this.demonstration.steps[steps.indexOf(write)].text;
            if (after !== original) throw new Error('The observed value does not match the written input.');
        }
        return { name: output.name, selector, field: output.field,
            check: parameter ? { kind: 'equals_parameter', parameter }
                : !existed && !selector.namePrefix ? { kind: 'exists' } : { kind: 'changed' } };
    }

    // Learning and reuse prove the window of their demonstration with a fresh native list, so a replaced window is refused.
    async requireWindowInstance(pid, windowHandle) {
        await this.requireApplicationInstance(pid);
        const result = this.requireSuccess(await this.native('list_windows'));

        return requireInstanceWindow(result.windows.filter(window => window.ProcessId === pid), windowHandle);
    }

    checkLearningIdentity(pid, entry = null) {
        if (!this.knownPids.has(pid)) throw new Error('Begin with Info and select a currently observed application.');
        if (!this.serverInfo?.version || this.platform === 'unknown') throw new Error('Read native node Info before learning or execution.');
        if (this.learned.application.identity_source === 'native-version'
            && !this.applicationWindows.some(window => window.pid === pid && window.version === this.learned.application.version
                && window.process_name === this.learned.application.process_name)) {
            throw new Error('Native application version does not match this cartridge.');
        }
        if (entry) {
            const result = this.compatibilityRuntime.assess(entry);
            if (result.status !== 'reusable') throw new Error('Generated function compatibility changed: ' + result.reason + '; revalidate before input.');
        }
    }

    async executeFunction(entry, args) {
        const parameters = validateFunctionArguments(entry, args);
        this.checkLearningIdentity(args.pid, entry);
        const visible = new Map(this.projection.references);
        let completed = 0;
        let phase = 'preflight';
        let reply = null;
        let started = false;
        try {
            const bindings = await this.bindFunctionWindows(entry, args.pid, args.window);
            const before = await this.preflightFunction(entry, args.pid, bindings);
            started = true;
            const instanceWindow = await this.processInstanceWindow(args.pid, args.window);
            const baseline = await this.recordBaseline(args.pid, instanceWindow);
            phase = 'execution';
            for (const [index, step] of entry.steps.entries()) {
                this.operationRecorder?.step(index);
                await this.requireApplicationInstance(args.pid);
                if (entry.steps.slice(0, index).some(previous => previous.action === 'write')) {
                    const fresh = await this.readTrees(args.pid, bindings);
                    this.requirePriorWrites(entry.steps.slice(0, index), parameters, fresh);
                }
                const window = bindings.get(step.selector?.window)?.WindowHandle;
                await this.performIdentity(args.pid, { action: step.action, text: parameters[step.parameter] }, step.selector,
                    step.route, true, window);
                completed++;
                if (step.action === 'write') {
                    phase = 'verification';
                    await this.verifyWrittenSelector(args.pid, step.selector, parameters[step.parameter], window);
                    phase = 'execution';
                }
            }
            const after = await this.readTrees(args.pid, bindings, entry.steps.some(step => step.action === 'select'));
            if (args.result_mode !== undefined) phase = 'verification';
            const outputs = this.verifyOutputs(entry, parameters, before, after, args.result_mode === undefined);
            const changes = await this.changesSince(args.pid, instanceWindow, baseline);
            const result = { success: true, function: entry.name, completed_steps: completed, verified: true, outputs,
                steps: entry.steps.map(stepLabel), ...changes };
            if (args.result_mode !== undefined) {
                result.unconfirmed_effects = entry.steps.flatMap((step, index) => step.action === 'write'
                    ? [] : [{ step: index + 1, action: step.action }]);
            }
            const checks = Object.fromEntries(entry.outputs.map(output => [output.name,
                { verified: true, ...(output.check.kind === 'equals_parameter' ? { matches_input: true } : {}) }]));
            reply = formatResult(this.results, this.resultNode, result, args, checks);

            return reply;
        } catch (error) {
            const result = { success: false, function: entry.name, completed_steps: completed, verified: false, phase,
                input_sent: phase === 'preflight' ? false : 'possibly',
                error: error.message, retry: 'Do not replay mutations automatically; inspect current state.' };
            reply = formatResult(this.results, this.resultNode, result, args);

            return reply;
        } finally {
            // A refused call never reached the window, so it leaves the references and the next full view as they were.
            if (started) this.hideAfterFunction(visible, args.pid, reply);
        }
    }

    // The window a stored function observes is the one its window argument names, or else the process's first window.
    async processInstanceWindow(pid, handle = undefined) {
        const result = this.requireSuccess(await this.native('list_windows'));
        const ofProcess = result.windows.filter(window => window.ProcessId === pid);
        const instance = handle === undefined ? ofProcess[0] : ofProcess.find(window => window.WindowHandle === handle);
        if (!instance) throw new Error(unknownWindowRefusal);

        return instance.WindowHandle;
    }

    // A verified run keeps its outcome when its change report cannot be read; the reply then asks for an observation instead.
    async changesSince(pid, window, baseline) {
        try {
            return { ui: await this.observe({ pid, window, since_view: baseline }) };
        } catch (error) {
            return { observation_required: true, observation_reason: 'changes_unreadable', ui_error: error.message };
        }
    }

    // The window is read completely before any input, so the reply can describe only what the function changed.
    async recordBaseline(pid, window) {
        let view = await this.observe({ pid, window, full: true, limit: viewLimits.maximumPage });
        while (view.next_cursor) view = await this.observe({ pid, window, cursor: view.next_cursor, limit: viewLimits.maximumPage });

        return view.view_ref;
    }

    // Only references the reply showed stay usable; the baseline's rows were never shown, so the next full view starts afresh.
    hideAfterFunction(visible, pid, reply) {
        const shown = new Map(visible);
        for (const row of [...(reply?.ui?.elements ?? []), ...(reply?.ui?.changed ?? [])]) {
            const target = this.projection.references.get(row.ref);
            if (target) shown.set(row.ref, target);
        }
        this.projection.hideUnseen(shown, pid);
    }

    requirePriorWrites(steps, parameters, trees) {
        const writes = new Map();
        for (const step of steps) {
            if (step.action === 'write') writes.set(JSON.stringify(step.selector), step);
        }
        for (const step of writes.values()) {
            const nodes = trees.get(step.selector.window) ?? [];
            const current = readOptionalOutput(nodes, step.selector, 'value');
            if (current.exists) this.requireWrittenValue(nodes, step.selector, parameters[step.parameter]);
        }
    }

    async preflightFunction(entry, pid, bindings) {
        this.checkLearningIdentity(pid);
        if (entry.compatibility.format) {
            const result = this.requireSuccess(await this.native('get_machine_capabilities'));
            await this.compatibilityRuntime.observe(result.capabilities ?? {});
        }
        await this.requireApplicationInstance(pid);
        this.checkLearningIdentity(pid, entry);
        const trees = await this.readTrees(pid, bindings);
        for (const [index, step] of entry.steps.entries()) {
            if (step.action === 'read') continue;
            const requireTarget = step.enabledBeforeFirstAction === false ? presentTarget : observedTarget;
            try { requireTarget(trees.get(step.selector.window) ?? [], step.selector, true); }
            catch (error) { throw new Error('Preflight step ' + (index + 1) + ': ' + error.message
                + ' All input targets must exist before execution; split workflows with later appearing controls.'); }
        }
        // Only a call whose window binding and targets were proved selects its process, as the compact gateway does.
        this.selectedPid = pid;
        return trees;
    }

    // Mirrors the compact route: every step kind binds one current window of that kind, a named window binds its own kind, and an
    // output kind binds only when its window exists. Several windows of one kind without a named window are refused.
    async bindFunctionWindows(entry, pid, handle = undefined) {
        const result = this.requireSuccess(await this.native('list_windows'));
        const ofProcess = result.windows.filter(window => window.ProcessId === pid);
        const named = handle === undefined ? undefined : ofProcess.find(window => window.WindowHandle === handle);
        if (handle !== undefined && !named) throw new Error(unknownWindowRefusal);
        const bindings = new Map();
        const stepKinds = entry.steps.filter(step => step.action !== 'read' && step.selector).map(step => step.selector.window);
        for (const kind of new Set(stepKinds)) {
            const matches = ofProcess.filter(window => window.Kind === kind);
            if (named?.Kind !== kind && matches.length !== 1) throw new Error(ambiguousWindowRefusal);
            bindings.set(kind, named?.Kind === kind ? named : matches[0]);
        }
        for (const output of entry.outputs) {
            const kind = output.selector.window;
            if (bindings.has(kind)) continue;
            const matches = ofProcess.filter(window => window.Kind === kind);
            if (named?.Kind === kind) bindings.set(kind, named);
            else if (matches.length > 1) throw new Error('Recipe output window is ambiguous.');
            else if (matches.length === 1) bindings.set(kind, matches[0]);
        }
        return bindings;
    }

    // One tree per bound window kind; a step reads only the tree of the window it is bound to.
    async readTrees(pid, bindings, fieldMetadata = false) {
        const trees = new Map();
        for (const [kind, window] of bindings) trees.set(kind, (await this.readTree(pid, fieldMetadata, window.WindowHandle)).nodes);
        return trees;
    }

    async inspectFunction(args) {
        if (typeof args.function !== 'string' || Object.keys(args).some(key => !['pid', 'window', 'function'].includes(key))
            || args.pid !== undefined && !Number.isInteger(args.pid) || args.window !== undefined && !Number.isInteger(args.window)) {
            throw new Error('Invalid preflight arguments.');
        }
        const pid = this.selectWindow(args).pid;
        const entry = this.learned.get(args.function);
        try {
            const trees = await this.preflightFunction(entry, pid, await this.bindFunctionWindows(entry, pid, args.window));
            const targets = entry.steps.map((step, index) => {
                const current = stepTargetNode(trees.get(step.selector.window) ?? [], step);
                return { step: index + 1, action: step.action, role: current.role,
                    match: step.selector.autoId ? 'automation_id+role+window' : 'name+role+window',
                    name_changed: current.name !== step.selector.name };
            });
            return { success: true, ready: true, function: entry.name, input_sent: false, targets };
        } catch (error) {
            return { success: false, ready: false, function: entry.name, input_sent: false, error: error.message };
        }
    }

    verifyOutputs(entry, parameters, before, after, legacyBound = true) {
        const outputs = {};
        for (const output of entry.outputs) {
            const kind = output.selector.window;
            const value = readOutput(after.get(kind) ?? [], output.selector, output.field);
            const previous = readOptionalOutput(before.get(kind) ?? [], output.selector, output.field).value;
            if (output.check.kind === 'equals_parameter' && value !== parameters[output.check.parameter]
                || output.check.kind === 'changed' && value === previous
                || output.check.kind === 'not_empty' && !value?.trim()) throw new Error('Observed output did not satisfy the learned postcondition.');
            if (typeof value !== 'string' || legacyBound && value.length > 16000) {
                throw new Error('Output exceeds the verified bounded text contract.');
            }
            outputs[output.name] = value;
        }
        return outputs;
    }
}

// The parameter names the stored call schema accepts, in the order the demonstration declared them.
function declaredParameters(entry) {
    return Object.keys(entry.inputSchema.properties.parameters.properties);
}

// The Use category and each of its subtopics, the same rule the compact gateway applies to stored functions.
function isUseTopic(topic) {
    return topic === 'Use' || typeof topic === 'string' && topic.startsWith('Use/');
}

function selectorOf(target) {
    if (!target || !target.windowKind) throw new Error('Learning requires an observed owning window identity.');
    return { role: target.role, name: target.name, autoId: target.autoId, window: target.windowKind,
        ...(target.containerPath ? { containerPath: target.containerPath } : {}) };
}

// A flagged step's target may still be disabled until its earlier step runs, so inspection checks only its presence.
function stepTargetNode(nodes, step) {
    if (step.enabledBeforeFirstAction === false) return presentTarget(nodes, step.selector, true);
    return nodes.find(node => node.id === observedTarget(nodes, step.selector, true));
}

// Only a later step can wait for an earlier action; it is flagged when its unique first-observation target was disabled.
function disabledBeforeFirstAction(demonstration, index) {
    try {
        return !presentTarget(demonstration.before, selectorOf(demonstration.bindings[index]), true).enabled;
    } catch {
        return false;
    }
}

// The stored-function codec refuses a false flag without an earlier non-read step, so only such a step may be recorded as disabled.
function mayWaitForEarlierMutation(demonstration, index) {
    return demonstration.steps[index].action !== 'read' && demonstration.steps.slice(0, index).some(step => step.action !== 'read');
}

function validateFunctionArguments(entry, args) {
    validateResultOptions(args, entry.outputs.map(output => output.name));
    const schema = entry.inputSchema.properties.parameters;
    // A function that declares no parameters may omit the parameters object; a function with parameters still requires it.
    const parameters = args.parameters ?? (Object.keys(schema.properties).length === 0 ? {} : undefined);
    if (Object.keys(args).some(key => !['pid', 'window', 'parameters', 'result_mode', 'fields'].includes(key))
        || !parameters || typeof parameters !== 'object' || Array.isArray(parameters)
        || Object.keys(parameters).some(key => !Object.hasOwn(schema.properties, key))) {
        throw new Error('Function arguments do not match the loaded schema.');
    }
    for (const key of schema.required) {
        if (!Object.hasOwn(parameters, key) || typeof parameters[key] !== 'string' || parameters[key].length > 16000) {
            throw new Error('Supply every bounded text parameter.');
        }
    }
    return parameters;
}

// The native arguments with the postcondition added when there is one.
function withPostcondition(target, postcondition) {
    return postcondition === null ? target : { ...target, postcondition };
}
