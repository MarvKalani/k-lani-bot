import test from 'node:test';
import assert from 'node:assert/strict';
import { UiProjection, parseNativeTree, observedTarget, semanticTarget, stableSelector } from './projection.mjs';

const row = (id, role, name, value = '', offscreen = false, depth = 0) => ' '.repeat(depth)
    + `[${id}] ${role} ${JSON.stringify(name)} (autoId: "", val: ${JSON.stringify(value)}, enabled: True, offscreen: ${offscreen ? 'True' : 'False'})`;

// The native list_windows row of the one window that a tree was read for; only its handle, lifetime and kind matter to projection.
const observedWindow = (kind = 'main', handle = 100, started = 'start') => ({ WindowHandle: handle, ProcessStartedAtUtc: started, Kind: kind });

test('A then B then unchanged A retains its own completed baseline', context => {
    const projection = new UiProjection();
    const a = parseNativeTree(row(1, 'Edit', 'A', 'same'.repeat(400)));
    const first = projection.observe(10, a);
    projection.observe(20, parseNativeTree(row(2, 'Edit', 'B', 'other')));
    const again = projection.observe(10, a, { since_view: first.view_ref });
    assert.equal(again.kind, 'delta');
    assert.deepEqual(again.changed, []);
    assert.deepEqual(again.removed, []);
    assert.equal(again.next_cursor, null);
    const fullBytes = Buffer.byteLength(JSON.stringify(first));
    const deltaBytes = Buffer.byteLength(JSON.stringify(again));
    assert.ok(deltaBytes <= fullBytes * 0.6);
    context.diagnostic(JSON.stringify({ aba_full_bytes: fullBytes, aba_delta_bytes: deltaBytes }));
});

test('external changes remain deltas, while other filters/nodes and expired baselines get full views with a reason', () => {
    let now = 0;
    const projection = new UiProjection({ clock: () => now });
    const first = projection.observe(10, parseNativeTree(row(1, 'Edit', 'A', 'first')));
    projection.observe(20, parseNativeTree(row(2, 'Edit', 'B', 'other')));
    const changed = projection.observe(10, parseNativeTree(row(3, 'Edit', 'A', 'second')), { since_view: first.view_ref });
    assert.equal(changed.changed[0].value, 'second');
    const filtered = projection.observe(10, parseNativeTree(row(4, 'Edit', 'A', 'second')), { max_chars: 3, since_view: changed.view_ref });
    assert.equal(filtered.kind, 'full');
    assert.equal(filtered.baseline_reason, 'context_mismatch');
    const foreign = projection.observe(10, parseNativeTree(row(5, 'Edit', 'A', 'second')), { node: 'other', since_view: changed.view_ref });
    assert.equal(foreign.kind, 'full');
    now += 300000;
    assert.throws(() => projection.target(first.elements[0].ref, 10), /Unknown reference/);
    const expired = projection.observe(10, parseNativeTree(row(6, 'Edit', 'A', 'second')), { since_view: changed.view_ref });
    assert.equal(expired.kind, 'full');
    projection.clear();
    assert.throws(() => projection.target(expired.elements[0].ref, 10), /Unknown reference/);
});

test('IDs remain stable through caption changes of the observed window; container paths disambiguate repeated IDs', () => {
    const projection = new UiProjection();
    const build = (title, name) => parseNativeTree([row(1, 'Window', title), row(2, 'group', 'Left', '', false, 2),
        row(3, 'Edit', name, '', false, 4).replace('autoId: ""', 'autoId: "field"'),
        row(4, 'group', 'Right', '', false, 2), row(5, 'Edit', 'Other', '', false, 4).replace('autoId: ""', 'autoId: "field"')].join('\n'));
    const options = { instance: 'verified-window-instance', window: observedWindow() };
    const first = projection.observe(10, build('Old title', 'Old caption'), options);
    const ref = first.elements.find(element => element.name === 'Old caption').ref;
    const next = projection.observe(10, build('New title', 'New caption'), options);
    assert.equal(next.changed.find(element => element.name === 'New caption').ref, ref);
    const target = projection.target(ref, 10);
    assert.equal(target.windowKind, 'main');
    assert.equal(observedTarget(build('New title', 'New caption'), target), 3);
    const duplicated = build('New title', 'New caption');
    duplicated.splice(3, 0, { ...duplicated[2], id: 6 });
    assert.throws(() => observedTarget(duplicated, target), /ambiguous/);
    projection.observe(10, duplicated, options);
    assert.throws(() => projection.target(ref, 10), /ambiguous_target/);
});

test('a reference keeps its observed window handle and kind while the caption changes within the same window lifetime', () => {
    const projection = new UiProjection();
    const nodes = title => parseNativeTree([row(1, 'Window', title), row(2, 'Edit', 'Field', '', false, 2)].join('\n'));
    const first = projection.observe(10, nodes('Original'), { window: observedWindow(), offscreen: true });
    const ref = first.elements.find(element => element.name === 'Field').ref;
    projection.observe(10, nodes('Changed'), { window: observedWindow(), offscreen: false });
    const target = projection.target(ref, 10);
    assert.equal(target.windowKind, 'main');
    assert.equal(target.windowHandle, 100);
    assert.equal(target.processStarted, 'start');
});

test('250 controls page without omissions and a changed observation invalidates a remaining cursor', () => {
    const projection = new UiProjection();
    const nodes = Array.from({ length: 250 }, (_, index) => parseNativeTree(row(index + 1, 'Edit', 'Field ' + index))[0]);
    let page = projection.observe(10, nodes);
    const collected = [...page.elements];
    while (page.next_cursor) {
        page = projection.observe(10, nodes, { cursor: page.next_cursor });
        collected.push(...page.elements);
    }
    assert.equal(collected.length, 250);
    assert.equal(new Set(collected.map(element => element.ref)).size, 250);
    const pending = projection.observe(10, nodes, { full: true });
    nodes[249].value = 'external change';
    assert.throws(() => projection.observe(10, nodes, { cursor: pending.next_cursor }), /stale_cursor/);
});

test('native desktop ancestors do not become part of a window-relative semantic selector', () => {
    const nodes = parseNativeTree([row(1, 'Root', 'Accessibility desktop'), row(2, 'Window', 'Form', '', false, 2),
        row(3, 'panel', 'Fields', '', false, 4), row(4, 'Edit', 'Value', '', false, 6)].join('\n'));
    const projection = new UiProjection();
    const view = projection.observe(10, nodes, { window: observedWindow() });
    const window = projection.target(view.elements.find(element => element.role === 'Window').ref, 10);
    const target = projection.target(view.elements.find(element => element.role === 'Edit').ref, 10);
    assert.equal(observedTarget(nodes, window, true), 2);
    assert.equal(observedTarget(nodes, target, true), 4);
    assert.deepEqual(target.containerPath, [{ role: 'panel', name: 'Fields' }]);
});

test('projection keeps application results, offscreen controls and literal Unicode while dropping browser chrome and duplicates', () => {
    const source = [row(1, 'Window', 'Browser'), row(2, 'button', 'Browser toolbar', '', false, 2),
        row(3, 'document web', 'Application', '￼', false, 2), row(4, 'panel', '', '￼￼', false, 4),
        row(5, 'button', 'Addieren', 'Addieren', false, 6), row(6, 'static', 'Addieren', 'Addieren', false, 8),
        row(7, 'Edit', 'Beträge', '', true, 6), row(8, 'static', 'Summe: 86.00 EUR', 'Summe: 86.00 EUR', false, 6)].join('\n');
    const view = new UiProjection().observe(10, parseNativeTree(source));
    assert.equal(view.elements.some(element => element.name === 'Browser toolbar'), false);
    assert.equal(view.elements.filter(element => element.name === 'Addieren').length, 1);
    assert.equal(view.elements.find(element => element.name === 'Beträge').offscreen, true);
    assert.equal(view.elements.some(element => element.name === 'Summe: 86.00 EUR'), true);
    assert.equal(JSON.stringify(view).includes('\\u00'), false);
});

test('a tree read for one window resolves targets inside it; several outer windows name no single window', () => {
    const target = { role: 'button', name: 'Delete', autoId: '' };
    const single = parseNativeTree([row(1, 'Window', 'Inbox'), row(2, 'button', 'Delete', '', false, 2)].join('\n'));
    assert.equal(observedTarget(single, target), 2);
    const several = parseNativeTree([row(1, 'Window', 'Inbox'), row(2, 'button', 'Delete', '', false, 2),
        row(3, 'Window', 'Inbox archive'), row(4, 'button', 'Delete', '', false, 2)].join('\n'));
    assert.throws(() => observedTarget(several, target), /window identity/);
});

test('a stored selector never depends on the caption of its window, including unsaved-changes markers', () => {
    const build = title => parseNativeTree([row(1, 'Window', title), row(2, 'Edit', 'Field', '', false, 2)].join('\n'));
    const target = { role: 'Edit', name: 'Field', autoId: '', windowKind: 'main' };
    for (const title of ['Editor', '*Editor', 'Other', '**Editor', 'Editor *']) {
        assert.equal(observedTarget(build(title), target), 2, title);
    }
});

test('learned Automation ID matching tolerates a changed caption while window scope remains strict', () => {
    const single = parseNativeTree([row(1, 'Window', 'Inbox'), row(2, 'button', 'Löschen', '', false, 2)
        .replace('autoId: ""', 'autoId: "delete"')].join('\n'));
    const target = { role: 'button', name: 'Delete', autoId: 'delete', windowKind: 'main' };
    assert.throws(() => observedTarget(single, target), /absent/);
    assert.equal(observedTarget(single, target, true), 2);
    const several = parseNativeTree([row(1, 'Window', 'Inbox'), row(2, 'button', 'Löschen', '', false, 2)
        .replace('autoId: ""', 'autoId: "delete"'), row(3, 'Window', 'Inbox archive'),
        row(4, 'button', 'Delete', '', false, 2).replace('autoId: ""', 'autoId: "delete"')].join('\n'));
    assert.throws(() => observedTarget(several, target, true), /window identity/);
});

test('nested rows of a same-titled window frame belong to the outermost window, so the inner control stays unique', () => {
    const nodes = parseNativeTree([row(1, 'Window', 'Rechner'), row(2, 'Window', 'Rechner', '', false, 2),
        row(3, 'button', 'Eins', '', false, 4).replace('autoId: ""', 'autoId: "one"')].join('\n'));
    assert.equal(observedTarget(nodes, { role: 'button', name: 'Eins', autoId: 'one' }, true), 3);
});

test('a stable selector of a control is the same when only the caption of its observed window changes', () => {
    const window = observedWindow();
    const control = name => [row(1, 'Window', name), row(2, 'button', 'Save', '', false, 2).replace('autoId: ""', 'autoId: "save"')];
    const first = semanticTarget(10, parseNativeTree(control('Fixture').join('\n')), parseNativeTree(control('Fixture').join('\n'))[1], window);
    const renamed = semanticTarget(10, parseNativeTree(control('*Fixture').join('\n')), parseNativeTree(control('*Fixture').join('\n'))[1],
        window);
    assert.deepEqual(stableSelector(first), stableSelector(renamed));
    assert.equal(stableSelector(first).window, undefined);
    assert.equal(stableSelector(first).windowHandle, 100);
});

test('a visible modal dialog remains observable instead of being hidden by document compaction', () => {
    const source = [row(1, 'Window', 'Browser'), row(2, 'document web', 'Page', '', false, 2),
        row(3, 'button', 'Underlying action', '', false, 4), row(4, 'dialog', 'Confirm'),
        row(5, 'button', 'Cancel', '', false, 2)].join('\n');
    const projection = new UiProjection();
    const view = projection.observe(10, parseNativeTree(source), { window: observedWindow() });
    assert.equal(view.elements.some(element => element.name === 'Confirm'), true);
    assert.equal(view.elements.some(element => element.name === 'Underlying action'), false);
    const cancel = view.elements.find(element => element.name === 'Cancel');
    assert.equal(projection.target(cancel.ref, 10).windowKind, 'main');
});

test('new native IDs do not resend an unchanged view; changing text remains observable and clipping is explicit', () => {
    const projection = new UiProjection();
    const first = projection.observe(10, parseNativeTree(row(1, 'Edit', 'Field', 'original')), { window: observedWindow() });
    const second = projection.observe(10, parseNativeTree(row(20, 'Edit', 'Field', 'original')), { window: observedWindow() });
    assert.deepEqual(second.changed, []);
    assert.deepEqual(second.removed, []);
    assert.equal(projection.target(first.elements[0].ref, 10).name, 'Field');
    const third = projection.observe(10, parseNativeTree(row(30, 'Edit', 'Field', 'changed')), { window: observedWindow() });
    assert.equal(third.changed[0].value, 'changed');
    const clipped = projection.observe(10, parseNativeTree(row(40, 'Edit', 'Field', 'long value')), { max_chars: 4, window: observedWindow() });
    assert.equal(clipped.elements[0].value_truncated, true);
    assert.throws(() => projection.target(first.elements[0].ref, 11), /Unknown reference/);
    const literal = projection.observe(10, parseNativeTree(row(50, 'Edit', 'Field', 'literal ￼ value')), { window: observedWindow() });
    assert.equal(literal.changed[0].value, 'literal ￼ value');
});

test('multiple document roots require explicit selection and unknown native formats fail closed', () => {
    const projection = new UiProjection();
    const nodes = parseNativeTree([row(1, 'document web', 'First'), row(2, 'document web', 'Second')].join('\n'));
    assert.throws(() => projection.observe(10, nodes), /ambiguous/);
    assert.equal(projection.observe(10, nodes, { root: 'Second' }).elements[0].name, 'Second');
    assert.throws(() => parseNativeTree('Unknown format'), /Unsupported/);
    const bounded = new UiProjection();
    const page = bounded.observe(10, parseNativeTree([row(1, 'button', 'First'), row(2, 'button', 'Second')].join('\n')), { limit: 1 });
    assert.equal(page.truncated, true);
    assert.throws(() => bounded.target('e2', 10), /Unknown reference/);
});
