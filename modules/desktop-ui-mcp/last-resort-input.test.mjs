import test from 'node:test';
import assert from 'node:assert/strict';
import { DesktopUiGateway } from './gateway.mjs';
import { movedRefusal, outsideRefusal, shotRequiredRefusal } from './last-resort-input.mjs';

const reason = 'egui canvas without accessibility';
const image = { type: 'image', data: 'iVBORw0KGgo=', mimeType: 'image/png' };

// A node whose window publishes no tree unless withTree is set; geometry, screenshot and inputs answer like the real node.
function fixture({ withTree = false, refuse = null } = {}) {
    const calls = [];
    const state = { bounds: { x: 0, y: 0, width: 1280, height: 690 } };
    const client = { call: async (tool, args) => {
        calls.push({ tool, args });
        let result = { success: true };
        let content = [];
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, Title: 'Editor', WindowHandle: 100, Kind: 'main',
            ProcessStartedAtUtc: '2026-10-17T00:00:00Z' }];
        if (tool === 'get_ui_tree') {
            result = withTree ? { success: true, tree: '[1] Window "Editor"\n  [2] Button "Import"', truncated: true, snapshot_id: 's' }
                : { success: false, code: 'no_accessibility_tree', error: 'error: The accessibility bus shows no tree for this window.' };
        }
        if (tool === 'get_window_geometry') result.bounds = state.bounds;
        if (tool === 'take_screenshot') content = [image];
        if (['focus_window', 'mouse_click', 'hotkey', 'set_clipboard'].includes(tool)) result.outcome = 'dispatched';
        if (tool === refuse) result = { success: false, outcome: 'not_dispatched', code: 'denied',
            error: 'Node guard blocks raw keys, coordinates, shell and process authority in guarded mode.' };
        return { result: { structuredContent: result, content } };
    } };
    return { gateway: new DesktopUiGateway(client), calls, state };
}

test('ui_input without steps returns a screenshot and a shot without any input', async () => {
    const { gateway, calls } = fixture();
    await gateway.call('ui_info', {});
    const reply = await gateway.call('ui_input', { window: 100, reason });
    assert.equal(reply.success, true);
    assert.equal(reply.semantic_route, 'no_accessibility_tree');
    assert.deepEqual(reply.bounds, { x: 0, y: 0, width: 1280, height: 690 });
    assert.equal(typeof reply.shot, 'string');
    assert.deepEqual(reply.images, [image]);
    assert.equal(calls.some(call => ['focus_window', 'mouse_click', 'hotkey'].includes(call.tool)), false);
});

test('a window with an accessibility tree is refused before any input', async () => {
    const { gateway, calls } = fixture({ withTree: true });
    await gateway.call('ui_info', {});
    const reply = await gateway.call('ui_input', { window: 100, reason, steps: [{ action: 'key', keys: ['enter'] }] });
    assert.equal(reply.success, false);
    assert.match(reply.error, /has an accessibility tree/);
    assert.equal(calls.some(call => ['focus_window', 'hotkey'].includes(call.tool)), false);
});

test('a click needs the latest shot, an unmoved window and a point inside it', async () => {
    const { gateway, calls, state } = fixture();
    await gateway.call('ui_info', {});
    await assert.rejects(gateway.call('ui_input', { window: 100, reason, steps: [{ action: 'click', x: 5, y: 5 }] }),
        { message: shotRequiredRefusal });
    const look = await gateway.call('ui_input', { window: 100, reason });
    const stale = await gateway.call('ui_input', { window: 100, reason, shot: 'other', steps: [{ action: 'click', x: 5, y: 5 }] });
    assert.equal(stale.error, shotRequiredRefusal);
    const fresh = await gateway.call('ui_input', { window: 100, reason });
    const outside = await gateway.call('ui_input', { window: 100, reason, shot: fresh.shot, steps: [{ action: 'click', x: 1280, y: 5 }] });
    assert.equal(outside.error, outsideRefusal);
    const again = await gateway.call('ui_input', { window: 100, reason });
    state.bounds = { x: 10, y: 0, width: 1280, height: 690 };
    const moved = await gateway.call('ui_input', { window: 100, reason, shot: again.shot, steps: [{ action: 'click', x: 50, y: 5 }] });
    assert.equal(moved.error, movedRefusal);
    assert.notEqual(look.shot, fresh.shot);
    assert.equal(calls.some(call => ['focus_window', 'mouse_click'].includes(call.tool)), false);
});

test('a click and typed text reach the node by window handle with the journaled reason and no decision', async () => {
    const { gateway, calls } = fixture();
    await gateway.call('ui_info', {});
    const look = await gateway.call('ui_input', { window: 100, reason });
    const reply = await gateway.call('ui_input', { window: 100, reason, shot: look.shot,
        steps: [{ action: 'click', x: 120, y: 468, button: 'right' }, { action: 'type', text: '/workspace' }, { action: 'key', keys: ['enter'] }] });
    assert.equal(reply.success, true);
    assert.equal(reply.completed_steps, 3);
    const focus = calls.find(call => call.tool === 'focus_window');
    assert.deepEqual(focus.args, { pid: 10, window: 100 });
    const click = calls.find(call => call.tool === 'mouse_click');
    assert.deepEqual(click.args, { x: 120, y: 468, button: 'right', last_resort_reason: 'no_accessibility_tree: ' + reason });
    assert.deepEqual(calls.find(call => call.tool === 'set_clipboard').args, { text: '/workspace' });
    const keys = calls.filter(call => call.tool === 'hotkey').map(call => call.args.keys);
    assert.deepEqual(keys, [['ctrl', 'v'], ['enter']]);
    assert.equal(calls.some(call => call.args?.decision !== undefined || call.args?.title !== undefined), false);
    assert.equal(gateway.demonstration, null);
});

test('a node refusal in the guarded profile surfaces and invalidates the shot', async () => {
    const { gateway } = fixture({ refuse: 'hotkey' });
    await gateway.call('ui_info', {});
    const look = await gateway.call('ui_input', { window: 100, reason });
    const reply = await gateway.call('ui_input', { window: 100, reason, steps: [{ action: 'key', keys: ['ctrl', 'a'] }] });
    assert.equal(reply.success, false);
    assert.match(reply.error, /guarded mode/);
    assert.equal(reply.completed_steps, 0);
    const click = await gateway.call('ui_input', { window: 100, reason, shot: look.shot, steps: [{ action: 'click', x: 5, y: 5 }] });
    assert.equal(click.error, shotRequiredRefusal);
});

test('a click after the first step and foreign fields are refused before any node call', async () => {
    const { gateway, calls } = fixture();
    await gateway.call('ui_info', {});
    const before = calls.length;
    await assert.rejects(gateway.call('ui_input', { window: 100, reason, shot: 's',
        steps: [{ action: 'key', keys: ['tab'] }, { action: 'click', x: 1, y: 1 }] }), /Only the first step/);
    await assert.rejects(gateway.call('ui_input', { window: 100, reason, steps: [{ action: 'key', keys: ['tab'], text: 'x' }] }),
        /takes keys and nothing else/);
    assert.equal(calls.length, before);
});
