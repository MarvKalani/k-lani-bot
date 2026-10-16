import test from 'node:test';
import assert from 'node:assert/strict';
import { DesktopUiGateway } from './gateway.mjs';

function fixture() {
    let revision = 0;
    let model = 'Basic';
    let width = '900 mm';
    let panels = 'None';
    let glass = null;
    let ignored = false;
    let required = true;
    let disabled = false;
    let readOnly = false;
    let now = 0;
    let manyOptions = false;
    let unknownDependencies = false;
    let delayedGlass = false;
    let textField = false;
    const mutations = [];
    const definitions = () => [
        { name: 'Door model', key: 'model', value: model, choices: manyOptions ? Array.from({ length: 150 }, (_, index) => 'Option ' + index)
            : ['Basic', 'Wide'], dependencies: unknownDependencies ? null : ['width', 'panels', 'glass'] },
        { name: 'Door width', key: 'width', value: width, choices: model === 'Basic' ? ['900 mm', '1000 mm'] : ['1000 mm', '1200 mm'], dependencies: ['panels', 'glass'] },
        { name: 'Side panels', key: 'panels', value: panels, choices: width === '900 mm' ? ['None'] : width === '1200 mm'
            ? ['None', 'Left', 'Right', 'Both'] : ['None', 'Left', 'Right'], dependencies: ['glass'] },
        { name: 'Left glass', key: 'glass', value: glass, choices: model === 'Basic' ? ['Clear', 'Frosted'] : ['Clear', 'Frosted', 'Security'], dependencies: [] }
    ];
    const visibleGlass = () => ['Left', 'Both'].includes(panels);
    const client = { call: async (tool, args) => {
        const result = { success: true };
        if (tool === 'get_machine_capabilities') result.capabilities = { platform: 'linux-x11-atspi' };
        if (tool === 'list_windows') result.windows = [{ ProcessId: 10, WindowHandle: 1, Kind: 'main', ProcessStartedAtUtc: '2026-10-08T00:00:00Z', Title: 'Door' }];
        if (tool === 'get_ui_tree') {
            result.snapshot_id = 'fresh';
            result.tree = '[1] Window "Door" (autoId: "", val: "", enabled: True, offscreen: False)';
            result.form_metadata = {};
            for (const [index, field] of definitions().entries()) {
                const id = index + 2;
                result.tree += '\n  [' + id + '] ' + (textField && index === 0 ? 'Edit' : 'combo box') + ' ' + JSON.stringify(field.name) + ' (autoId: ' + JSON.stringify(field.key)
                    + ', val: ' + JSON.stringify(field.value) + ', enabled: True, offscreen: False)';
                result.form_metadata[id] = { Support: 'Available', Source: 'ApplicationDeclaration', Field: field.key,
                    Context: String(revision), Visible: field.key !== 'glass' || visibleGlass() && !delayedGlass, Enabled: !disabled, ReadOnly: readOnly,
                    Required: field.key === 'glass' ? visibleGlass() : required, Dependencies: field.dependencies,
                    Choices: { Support: 'Available', Revision: JSON.stringify(field.choices), Complete: true, NeedsOpen: false,
                        Options: field.choices.map(label => ({ Id: label, Label: label, Enabled: true })), SelectedId: field.value } };
            }
        }
        if (tool === 'click_element') {
            mutations.push(args);
            if (!ignored && args.choice_action === 'select') {
                const field = definitions()[args.id - 2];
                assert.equal(args.options_revision, JSON.stringify(field.choices));
                assert.equal(args.field_context, String(revision));
                if (field.key === 'model') {
                    model = args.option_id;
                    if (!definitions()[1].choices.includes(width)) width = null;
                    if (!definitions()[3].choices.includes(glass)) glass = null;
                } else if (field.key === 'width') width = args.option_id;
                else if (field.key === 'panels') panels = args.option_id;
                else glass = args.option_id;
                revision++;
            }
        }
        if (tool === 'type_text') {
            mutations.push(args);
            if (!ignored) { model = args.text; revision++; }
        }
        return { result: { structuredContent: result } };
    } };
    const gateway = new DesktopUiGateway(client, () => {}, null, { clock: () => now });
    return { gateway, mutations, externalChange: () => { revision++; model = 'Wide'; }, ignoreSetter: () => { ignored = true; },
        setRequiredUnknown: () => { required = null; }, setDisabled: () => { disabled = true; }, setReadOnly: () => { readOnly = true; },
        useManyOptions: () => { manyOptions = true; }, useUnknownDependencies: () => { unknownDependencies = true; },
        delayGlass: () => { delayedGlass = true; }, showGlass: () => { delayedGlass = false; },
        useTextField: () => { textField = true; },
        expire: () => { now += 300000; } };
}

async function readForm(gateway) {
    await gateway.call('ui_info', {});
    const view = await gateway.call('ui_observe', { pid: 10 });
    const scope = view.elements.find(element => element.role === 'Window').ref;
    const form = await gateway.call('cartridge_call', { function: 'form.read', arguments: { pid: 10, scope } });
    return { form, scope };
}

async function select(gateway, form, name, label) {
    const field = form.fields.find(field => field.name === name);
    const options = await gateway.call('cartridge_call', { function: 'form.options', arguments: {
        form_ref: form.form_ref, field_ref: field.field_ref, context_rev: form.context_rev } });
    const option = options.options.find(option => option.label === label);
    return gateway.call('cartridge_call', { function: 'form.set', arguments: { form_ref: form.form_ref, field_ref: field.field_ref,
        context_rev: options.context_rev, options_rev: options.options_rev, option_id: option.option_id } });
}

test('forms are discoverable without learning; Basic/900 validates without hidden glass', async () => {
    const { gateway, mutations } = fixture();
    const browse = await gateway.call('cartridge_browse', { topic: 'Use/Forms' });
    assert.deepEqual(browse.functions.map(entry => entry.name), ['form.read', 'form.options', 'form.set', 'form.validate']);
    const { form } = await readForm(gateway);
    const validation = await gateway.call('cartridge_call', { function: 'form.validate', arguments: {
        form_ref: form.form_ref, context_rev: form.context_rev } });
    assert.equal(validation.ready, true);
    const glass = form.fields.find(field => field.name === 'Left glass');
    assert.equal(glass.visible, false);
    await assert.rejects(gateway.call('cartridge_call', { function: 'form.set', arguments: {
        form_ref: form.form_ref, field_ref: glass.field_ref, context_rev: form.context_rev, options_rev: glass.options_rev, option_id: 'Clear' } }), /hidden/);
    assert.equal(mutations.length, 0);
});

test('Wide/1200/both/security is verified with stable field refs and changing option/context revisions', async () => {
    const { gateway } = fixture();
    const initial = await readForm(gateway);
    const oldGlass = initial.form.fields.find(field => field.name === 'Left glass');
    let form = initial.form;
    const callRead = () => gateway.call('cartridge_call', { function: 'form.read', arguments: { pid: 10, scope: initial.scope } });
    for (const [name, label] of [['Door model', 'Wide'], ['Door width', '1200 mm'], ['Side panels', 'Both'], ['Left glass', 'Security']]) {
        const result = await select(gateway, form, name, label);
        assert.equal(result.verified, true);
        form = await callRead();
    }
    const glass = form.fields.find(field => field.name === 'Left glass');
    assert.equal(glass.field_ref, oldGlass.field_ref);
    assert.notEqual(glass.options_rev, oldGlass.options_rev);
    assert.notEqual(form.structure_rev, initial.form.structure_rev);
    const validation = await gateway.call('cartridge_call', { function: 'form.validate', arguments: {
        form_ref: form.form_ref, context_rev: form.context_rev } });
    assert.equal(validation.ready, true);
});

test('stale context/options, foreign refs, hidden/disabled/read-only and invalid types send no input', async () => {
    for (const scenario of ['external', 'readonly', 'disabled', 'options', 'foreign', 'literal', 'invalid']) {
        const setup = fixture();
        const { form } = await readForm(setup.gateway);
        const field = form.fields[0];
        const args = { form_ref: form.form_ref, field_ref: field.field_ref, context_rev: form.context_rev,
            options_rev: field.options_rev, option_id: 'Wide' };
        if (scenario === 'external') setup.externalChange();
        if (scenario === 'readonly') setup.setReadOnly();
        if (scenario === 'disabled') setup.setDisabled();
        if (scenario === 'options') args.options_rev = 'stale';
        if (scenario === 'foreign') args.form_ref = 'another-session';
        if (scenario === 'literal') { delete args.option_id; delete args.options_rev; args.value = 'Wide'; }
        if (scenario === 'invalid') args.extra = 'invalid';
        await assert.rejects(setup.gateway.call('cartridge_call', { function: 'form.set', arguments: args }));
        assert.equal(setup.mutations.length, 0, scenario);
    }
});

test('ignored setters are unconfirmed, unknown required remains unknown and TTL/disconnect invalidate refs', async () => {
    const setup = fixture();
    const { form } = await readForm(setup.gateway);
    setup.ignoreSetter();
    const result = await select(setup.gateway, form, 'Door model', 'Wide');
    assert.equal(result.success, false);
    assert.equal(result.status, 'unconfirmed');
    assert.equal(setup.mutations.length, 1);
    setup.setRequiredUnknown();
    const next = await setup.gateway.call('cartridge_call', { function: 'form.read', arguments: { pid: 10, scope: 'e1' } });
    const validation = await setup.gateway.call('cartridge_call', { function: 'form.validate', arguments: {
        form_ref: next.form_ref, context_rev: next.context_rev } });
    assert.equal(validation.ready, false);
    assert.ok(validation.unknown_constraints.length > 0);
    setup.expire();
    await assert.rejects(setup.gateway.call('cartridge_call', { function: 'form.validate', arguments: {
        form_ref: form.form_ref, context_rev: form.context_rev } }), /expired/);
    setup.gateway.disconnect();
    assert.equal(setup.gateway.formRuntime.forms.size, 0);
});

test('complete options paginate and external list/context changes invalidate old pages without input', async () => {
    const setup = fixture();
    setup.useManyOptions();
    const { form } = await readForm(setup.gateway);
    const args = { form_ref: form.form_ref, field_ref: form.fields[0].field_ref, context_rev: form.context_rev, limit: 100 };
    const first = await setup.gateway.call('cartridge_call', { function: 'form.options', arguments: args });
    assert.equal(first.options.length, 100);
    assert.equal(first.complete, false);
    const last = await setup.gateway.call('cartridge_call', { function: 'form.options', arguments: { ...args, cursor: first.next_cursor } });
    assert.equal(last.options.length, 50);
    assert.equal(last.complete, true);
    assert.equal(new Set([...first.options, ...last.options].map(option => option.option_id)).size, 150);
    const pending = await setup.gateway.call('cartridge_call', { function: 'form.options', arguments: args });
    setup.externalChange();
    await assert.rejects(setup.gateway.call('cartridge_call', { function: 'form.options', arguments: { ...args, cursor: pending.next_cursor } }), /stale/);
    assert.equal(setup.mutations.length, 0);
});

test('required missing values and delayed visibility remain invalid, while unknown dependencies invalidate the active branch', async () => {
    const setup = fixture();
    setup.useUnknownDependencies();
    const initial = await readForm(setup.gateway);
    const changed = await select(setup.gateway, initial.form, 'Door model', 'Wide');
    assert.equal(changed.invalidated_fields.length, initial.form.fields.length);
    const read = () => setup.gateway.call('cartridge_call', { function: 'form.read', arguments: { pid: 10, scope: initial.scope } });
    let form = await read();
    const validation = await setup.gateway.call('cartridge_call', { function: 'form.validate', arguments: {
        form_ref: form.form_ref, context_rev: form.context_rev } });
    assert.equal(validation.ready, false);
    assert.ok(validation.missing_required.length > 0);
    await select(setup.gateway, form, 'Door width', '1200 mm');
    form = await read();
    await select(setup.gateway, form, 'Side panels', 'Both');
    setup.delayGlass();
    form = await read();
    const glass = form.fields.find(field => field.name === 'Left glass');
    const before = setup.mutations.length;
    await assert.rejects(setup.gateway.call('cartridge_call', { function: 'form.set', arguments: {
        form_ref: form.form_ref, field_ref: glass.field_ref, context_rev: form.context_rev, options_rev: glass.options_rev, option_id: 'Security' } }), /hidden/);
    assert.equal(setup.mutations.length, before);
    assert.equal((await setup.gateway.call('cartridge_call', { function: 'form.validate', arguments: {
        form_ref: form.form_ref, context_rev: form.context_rev } })).ready, false);
    setup.showGlass();
    form = await read();
    assert.equal((await select(setup.gateway, form, 'Left glass', 'Security')).verified, true);
});

test('literal setters compare the full value and long unknown form text remains completely retrievable', async () => {
    const setup = fixture();
    setup.useTextField();
    const { form, scope } = await readForm(setup.gateway);
    const value = 'ä'.repeat(16000);
    const field = form.fields[0];
    const result = await setup.gateway.call('cartridge_call', { function: 'form.set', arguments: {
        form_ref: form.form_ref, field_ref: field.field_ref, context_rev: form.context_rev, value } });
    assert.equal(result.verified, true);
    const read = await setup.gateway.call('cartridge_call', { function: 'form.read', arguments: { pid: 10, scope } });
    const written = read.fields[0];
    assert.equal(written.value_truncated, true);
    assert.equal(written.value.length, 2000);
    let offset = 0;
    let actual = '';
    do {
        const page = await setup.gateway.call('cartridge_call', { function: 'Info.result', arguments: {
            evidence_ref: written.evidence_ref, section: written.section, offset, limit: 8000 } });
        actual += page.text;
        offset = page.next_offset;
        if (page.complete) break;
    } while (true);
    assert.equal(actual, value);
    setup.ignoreSetter();
    const failed = await setup.gateway.call('cartridge_call', { function: 'form.set', arguments: {
        form_ref: read.form_ref, field_ref: written.field_ref, context_rev: read.context_rev, value: 'ignored different value' } });
    assert.equal(failed.verified, false);
    assert.equal(failed.status, 'unconfirmed');
});
