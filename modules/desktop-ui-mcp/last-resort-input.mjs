// ui_input is the explicit last resort for a window without an accessibility tree. It proves the tree is absent, binds click
// coordinates to the latest screenshot of the same window instance at the same rectangle, sends input through the node (policy,
// consent, guard, backup and journal apply there) and returns a fresh screenshot. The words match the compact C# gateway.
import crypto from 'node:crypto';
import { chooseWindow } from './gateway.mjs';

export const inputRoute = 'last-resort keyboard and pointer input (ui_input) for windows without an accessibility tree';
export const noTreeHint = 'This window publishes no accessibility tree; operate it with ui_input {window, reason} as a last resort.';
const treePresentRefusal = 'This window has an accessibility tree; use ui_observe and ui_act. ui_input is only for windows without one.';
const treeUnknownRefusal = 'The accessibility tree of this window could not be read, so ui_input cannot prove that it is absent: ';
export const shotRequiredRefusal = 'A click needs the shot id of the latest ui_input screenshot of this window; call ui_input '
    + 'without steps first.';
export const movedRefusal = 'The window moved or changed size since that screenshot; call ui_input without steps for a fresh one.';
export const outsideRefusal = 'The point lies outside the selected window.';
const replacedRefusal = 'The observed application window was replaced. Refresh Info before input.';
const unknownWindowRefusal = 'The window handle is not one of the current windows; call ui_info again.';
const next = 'Check the screenshot for the result. Click coordinates are pixels of this screenshot; pass its shot with the next click.';
const retry = 'Do not replay completed steps; call ui_input without steps to see the current state.';
const settleMilliseconds = 400;
const treeProbeRows = 2;

// Throws for fields that do not belong to their action, a click after the first step, or a click without shot.
export function readInputRequest(args) {
    const steps = (args.steps ?? []).map(step => {
        const others = Object.keys(step).filter(key => key !== 'action');
        const allowed = { click: ['x', 'y', 'button'], key: ['keys'], type: ['text'] }[step.action];
        if (others.some(key => !allowed.includes(key))) throw new Error(fieldsRefusal(step.action));
        if (step.action === 'click' && (step.x === undefined || step.y === undefined)) throw new Error(fieldsRefusal('click'));
        if (step.action === 'key' && step.keys === undefined) throw new Error(fieldsRefusal('key'));
        if (step.action === 'type' && step.text === undefined) throw new Error(fieldsRefusal('type'));
        return { ...step, button: step.button ?? 'left' };
    });
    if (steps.slice(1).some(step => step.action === 'click')) {
        throw new Error('Only the first step may be a click, because its coordinates come from the screenshot.');
    }
    if (steps[0]?.action === 'click' && !args.shot) throw new Error(shotRequiredRefusal);

    return { reason: args.reason, shot: args.shot, steps };
}

function fieldsRefusal(action) {
    if (action === 'click') return 'A click takes x and y (and optionally button) and no keys or text.';
    return action === 'key' ? 'A key step takes keys and nothing else.' : 'A type step takes text and nothing else.';
}

export class LastResortInput {
    constructor(gateway) {
        this.gateway = gateway;
        this.completed = 0;
        this.dispatchesAtStart = gateway.possibleDispatches;
    }

    async run(args) {
        const request = readInputRequest(args);
        this.gateway.demonstration = null;
        try {
            const window = await this.currentWindow(args.pid, args.window);
            const absence = await this.requireNoTree(window);
            if (request.steps.length > 0) {
                this.requireClickTarget(request, window, await this.bounds(window));
                await this.sendSteps(request, window, absence);
                await new Promise(resolve => setTimeout(resolve, settleMilliseconds));
                await this.currentWindow(window.ProcessId, window.WindowHandle, window.ProcessStartedAtUtc);
            }
            return await this.reply(window, absence);
        } catch (error) {
            this.gateway.inputShot = null;
            return { success: false, error: error.message, completed_steps: this.completed,
                failed_step_may_have_changed_ui: this.gateway.possibleDispatches > this.dispatchesAtStart, retry };
        }
    }

    // Throws unless the handle is a current window of an instance that ui_info listed, and the same one when started is given.
    async currentWindow(pid, handle, started = undefined) {
        const result = this.gateway.requireSuccess(await this.gateway.native('list_windows'));
        const window = chooseWindow(result.windows, pid, handle);
        if (!window) throw new Error(unknownWindowRefusal);
        const listed = this.gateway.windowInstances.get(window.WindowHandle);
        const sameInstance = listed?.pid === window.ProcessId && typeof window.ProcessStartedAtUtc === 'string'
            && listed.started === window.ProcessStartedAtUtc && (started === undefined || started === window.ProcessStartedAtUtc);
        if (!sameInstance) throw new Error(replacedRefusal);
        return window;
    }

    async requireNoTree(window) {
        const result = await this.gateway.native('get_ui_tree', { pid: window.ProcessId, window: window.WindowHandle, max_depth: 1,
            max_nodes: treeProbeRows, row_format: 'common' });
        if (result.success !== true) {
            if (result.code === 'no_accessibility_tree') return 'no_accessibility_tree';
            throw new Error(treeUnknownRefusal + (result.error ?? 'unknown'));
        }
        const rows = String(result.tree ?? '').split('\n').filter(line => line.trim().length > 0).length;
        if (result.truncated === true || rows > 1) throw new Error(treePresentRefusal);
        return 'empty_accessibility_tree';
    }

    async bounds(window) {
        const result = this.gateway.requireSuccess(await this.gateway.native('get_window_geometry',
            { pid: window.ProcessId, window: window.WindowHandle }));
        const bounds = result.bounds;
        const complete = bounds && ['x', 'y', 'width', 'height'].every(key => Number.isInteger(bounds[key]))
            && bounds.width > 0 && bounds.height > 0;
        if (!complete) throw new Error('The node reported no complete window rectangle.');
        return { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height };
    }

    requireClickTarget(request, window, bounds) {
        const first = request.steps[0];
        if (first.action !== 'click') return;
        const shot = this.gateway.inputShot;
        if (!shot || shot.id !== request.shot || shot.handle !== window.WindowHandle || shot.started !== window.ProcessStartedAtUtc) {
            throw new Error(shotRequiredRefusal);
        }
        if (JSON.stringify(shot.bounds) !== JSON.stringify(bounds)) throw new Error(movedRefusal);
        const inside = first.x >= bounds.x && first.x < bounds.x + bounds.width && first.y >= bounds.y && first.y < bounds.y + bounds.height;
        if (!inside) throw new Error(outsideRefusal);
    }

    async sendSteps(request, window, absence) {
        const journaled = absence + ': ' + request.reason;
        this.requireSent(await this.gateway.native('focus_window', { pid: window.ProcessId, window: window.WindowHandle }));
        for (const step of request.steps) {
            await this.currentWindow(window.ProcessId, window.WindowHandle, window.ProcessStartedAtUtc);
            if (step.action === 'click') {
                this.requireSent(await this.gateway.native('mouse_click', { x: step.x, y: step.y, button: step.button,
                    last_resort_reason: journaled }));
            } else if (step.action === 'key') {
                this.requireSent(await this.gateway.native('hotkey', { keys: step.keys, last_resort_reason: journaled }));
            } else {
                this.requireSent(await this.gateway.native('set_clipboard', { text: step.text }));
                this.requireSent(await this.gateway.native('hotkey', { keys: ['ctrl', 'v'], last_resort_reason: journaled }));
            }
            this.completed++;
        }
    }

    requireSent(result) {
        this.gateway.requireSuccess(result);
        if (!['completed', 'verified', 'dispatched'].includes(result.outcome)) throw new Error('Native input outcome remains unconfirmed.');
    }

    async reply(window, absence) {
        const bounds = await this.bounds(window);
        const image = this.gateway.requireSuccess(await this.gateway.native('take_screenshot'));
        const shot = { id: crypto.randomUUID().replaceAll('-', ''), handle: window.WindowHandle, started: window.ProcessStartedAtUtc,
            bounds };
        this.gateway.inputShot = shot;
        return { success: true, pid: window.ProcessId, window: window.WindowHandle, kind: window.Kind,
            semantic_route: absence, completed_steps: this.completed, bounds, shot: shot.id, next, images: image.images };
    }
}
