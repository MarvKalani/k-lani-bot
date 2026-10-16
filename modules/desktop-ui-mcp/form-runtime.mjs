import { createHash, randomUUID } from 'node:crypto';
import { observedTarget, semanticTarget, stableSelector } from './projection.mjs';
import { validateFormArguments } from './form-contract.mjs';
import { roleVocabularyEntry } from './role-vocabulary.mjs';

const maximumForms = 32;
const maximumFields = 200;
const ttlMilliseconds = 300000;

// Routed form operations retain volatile, instance-bound state and use fresh native metadata before every input.
export class FormRuntime {
    constructor(gateway, clock = Date.now) {
        this.gateway = gateway;
        this.clock = clock;
        this.forms = new Map();
        this.cursors = new Map();
    }

    async call(name, args) {
        validateFormArguments(name, args);
        this.expire();
        if (name === 'form.read') return this.read(args);
        const form = this.requireForm(args.form_ref);
        await this.refresh(form);
        if (form.contextRev !== args.context_rev) throw new Error('stale_context');
        if (name === 'form.validate') return this.validate(form);
        const field = form.fields.find(field => field.ref === args.field_ref);
        if (!field) throw new Error('unknown_or_stale_field');
        if (name === 'form.options') return this.options(form, field, args);
        return this.set(form, field, args);
    }

    async read(args) {
        const scope = this.gateway.projection.target(args.scope, args.pid);
        if (!/Window|frame|dialog|document|group|panel|section/i.test(scope.role)) throw new Error('Select an observed form container.');
        const key = JSON.stringify({ pid: args.pid, selector: stableSelector(scope), started: scope.processStarted });
        let form = [...this.forms.values()].find(form => form.key === key);
        if (!form) {
            if (this.forms.size >= maximumForms) this.remove(this.forms.keys().next().value);
            form = { ref: randomUUID(), key, pid: args.pid, scope, fields: [], touched: this.clock() };
            this.forms.set(form.ref, form);
        }
        await this.refresh(form);
        const names = args.fields;
        if (names && names.some(name => form.fields.filter(field => matchesField(field, name)).length !== 1)) throw new Error('Unknown or ambiguous requested form field.');
        const selected = form.fields.filter(field => !names || names.some(name => matchesField(field, name)));
        const page = this.pageFields(form, selected, args);
        // Only a successful read selects its process, as the compact gateway's form read does.
        this.gateway.selectedPid = args.pid;
        return page;
    }

    async refresh(form) {
        await this.gateway.requireApplicationInstance(form.pid);
        const observation = await this.gateway.observeWindows(form.pid);
        const scope = await this.gateway.currentWindowTarget(form.pid, form.scope);
        const snapshot = await this.gateway.readTree(form.pid, true, scope.windowHandle);
        const observed = observation.windows.find(window => window.WindowHandle === scope.windowHandle);
        await this.gateway.requireApplicationInstance(form.pid);
        const rootId = observedTarget(snapshot.nodes, scope, true);
        const start = snapshot.nodes.findIndex(node => node.id === rootId);
        const root = snapshot.nodes[start];
        let end = start + 1;
        while (end < snapshot.nodes.length && snapshot.nodes[end].depth > root.depth) end++;
        const selected = snapshot.nodes.slice(start, end).filter(node => fieldType(node.role));
        if (selected.length > maximumFields) throw new Error('Form scope exceeds its field bound.');
        const previous = new Map(form.fields.map(field => [field.key, field]));
        const fields = selected.map(node => fieldOf(form.pid, snapshot, node, observed, previous));
        if (new Set(fields.map(field => field.key)).size !== fields.length) throw new Error('ambiguous_field');
        const structure = fingerprint(fields.map(field => ({ key: field.key, role: field.role, visible: field.visible,
            enabled: field.enabled, read_only: field.read_only, required: field.required })));
        const context = fingerprint({ structure, instance: form.key,
            fields: fields.map(field => ({ key: field.key, value: field.value, context: field.metadata.Context, options: field.optionsRev })) });
        if (form.contextRev && form.contextRev !== context) this.dropCursors(form.ref);
        form.change = fields.filter(field => JSON.stringify(publicField(field)) !== JSON.stringify(publicField(previous.get(field.key))))
            .map(field => field.ref);
        form.removed = form.fields.filter(field => !fields.some(current => current.key === field.key)).map(field => field.ref);
        form.fields = fields;
        form.structureRev = structure;
        form.contextRev = context;
        form.snapshot = snapshot.snapshot_id;
        form.touched = this.clock();
    }

    pageFields(form, fields, args) {
        const binding = 'fields/' + JSON.stringify({ form: form.ref, context: form.contextRev, fields: args.fields });
        const records = fields.map(publicField);
        const longValues = Object.fromEntries(fields.filter(field => typeof field.value === 'string' && Array.from(field.value).length > 2000)
            .map(field => [field.ref, field.value]));
        if (Object.keys(longValues).length) {
            const evidence = this.gateway.results.put(this.gateway.resultNode, longValues, JSON.stringify(records));
            for (const record of records) {
                if (Object.hasOwn(longValues, record.field_ref)) {
                    record.value = Array.from(record.value).slice(0, 2000).join('');
                    record.value_truncated = true;
                    record.evidence_ref = evidence;
                    record.section = 'outputs/' + record.field_ref;
                }
            }
        }
        const page = this.page(binding, records, args, form.ref);
        return { success: true, ...revisions(form), fields: page.items, complete: page.complete, next_cursor: page.next_cursor,
            changed: form.change, removed: form.removed };
    }

    async options(form, field, args) {
        if (field.type !== 'choice' || field.metadata.Choices?.Support !== 'Available') throw new Error('options_unsupported');
        const steps = [];
        if (field.metadata.Choices.NeedsOpen && args.cursor === undefined) {
            requireWritable(field, false);
            this.gateway.requireSuccess(await this.gateway.native('click_element', { id: field.nativeId, snapshot_id: form.snapshot, choice_action: 'open' }));
            steps.push({ action: 'open_dropdown', field_ref: field.ref, selection_performed: false });
            const selected = field.value;
            await this.refresh(form);
            field = form.fields.find(current => current.ref === field.ref);
            if (!field || field.value !== selected) return { success: false, status: 'unconfirmed', opened: true, error: 'Opening changed or lost selection.' };
        }
        const binding = JSON.stringify({ form: form.ref, field: field.ref, context: form.contextRev, options: field.optionsRev });
        const page = this.page(binding, field.options, args, form.ref);
        return { success: true, ...revisions(form), field_ref: field.ref, options_rev: field.optionsRev,
            options: page.items, complete: page.complete && field.metadata.Choices.Complete,
            next_cursor: page.next_cursor, native_complete: field.metadata.Choices.Complete, ui_steps: steps };
    }

    async set(form, field, args) {
        requireWritable(field, true);
        const mutation = prepareSet(field, args);
        try {
            this.gateway.requireSuccess(await this.gateway.native(mutation.tool,
                { id: field.nativeId, snapshot_id: form.snapshot, ...mutation.arguments }));
            await this.refresh(form);
            const actual = form.fields.find(current => current.ref === field.ref);
            if (!actual || actual.value !== mutation.expected) throw new Error('Full native field value did not match the requested input.');
            const dependencies = field.metadata.Dependencies;
            const invalidated = form.fields.filter(current => dependencies == null || dependencies.includes(current.metadata.Field))
                .map(current => current.ref);
            return { success: true, verified: true, ...revisions(form), field_ref: field.ref, invalidated_fields: invalidated,
                dependency_evidence: { source: field.source, status: dependencies == null ? 'unknown' : 'provider_declared',
                    checked_context: args.context_rev, temporal_inference: false } };
        } catch (error) {
            return { success: false, verified: false, status: 'unconfirmed', input_sent: 'possibly', error: error.message,
                ...revisions(form), retry: 'Observe current state; never automatically replay this mutation.' };
        }
    }

    validate(form) {
        const missing_required = [];
        const invalid_values = [];
        const unknown_constraints = [];
        for (const field of form.fields) {
            if (field.visible === false) {
                if (field.required === true) unknown_constraints.push(field.ref);
                continue;
            }
            if (field.required === true && (field.value == null || field.value === '')) missing_required.push(field.ref);
            if (field.required === 'unknown' || field.read_only === 'unknown' || field.visible === 'unknown'
                || field.metadata.Support !== 'Available') unknown_constraints.push(field.ref);
            if (field.type === 'choice') {
                if (field.metadata.Choices?.Support !== 'Available' || !field.metadata.Choices.Complete) unknown_constraints.push(field.ref);
                else if (field.value != null && !field.options.some(option => option.label === field.value)) invalid_values.push(field.ref);
            }
        }
        return { success: true, ...revisions(form), ready: missing_required.length + invalid_values.length + unknown_constraints.length === 0,
            missing_required, invalid_values, unknown_constraints: [...new Set(unknown_constraints)] };
    }

    page(binding, items, args, formRef) {
        let offset = 0;
        let limit = args.limit ?? 50;
        if (args.cursor !== undefined) {
            const cursor = this.cursors.get(args.cursor);
            if (!cursor || cursor.binding !== binding || args.limit !== undefined && args.limit !== cursor.limit) throw new Error('stale_cursor');
            offset = cursor.offset;
            limit = cursor.limit;
            this.cursors.delete(args.cursor);
        }
        const page = items.slice(offset, offset + limit);
        const complete = offset + page.length >= items.length;
        let next_cursor = null;
        if (!complete) {
            if (this.cursors.size >= 32) this.cursors.delete(this.cursors.keys().next().value);
            next_cursor = randomUUID();
            this.cursors.set(next_cursor, { binding, formRef, offset: offset + page.length, limit });
        }
        return { items: page, complete, next_cursor };
    }

    requireForm(reference) {
        const form = this.forms.get(reference);
        if (!form) throw new Error('unknown_or_expired_form');
        return form;
    }

    dropCursors(reference) {
        for (const [token, cursor] of this.cursors) if (cursor.formRef === reference) this.cursors.delete(token);
    }

    remove(reference) {
        this.dropCursors(reference);
        this.forms.delete(reference);
    }

    expire() {
        for (const [reference, form] of this.forms) if (this.clock() - form.touched >= ttlMilliseconds) this.remove(reference);
    }

    clear() {
        this.forms.clear();
        this.cursors.clear();
    }
}

function fingerprint(value) {
    return createHash('sha256').update(JSON.stringify(value)).digest('hex');
}

// A platform vocabulary entry decides alone, so an action or other non-form role is not matched by the broad patterns.
export function fieldType(role) {
    const entry = roleVocabularyEntry(role);
    if (entry) {
        if (entry.fieldKind === 'text' || entry.fieldKind === 'choice') return entry.fieldKind;
        return null;
    }
    if (/Edit|entry/i.test(role)) return 'text';
    if (/combo|list box/i.test(role)) return 'choice';
    return null;
}

function matchesField(field, name) {
    return name === field.ref || name === field.name || name === field.metadata.Field;
}

function fieldOf(pid, snapshot, node, observed, previous) {
    const target = semanticTarget(pid, snapshot.nodes, node, observed);
    const key = fingerprint(stableSelector(target));
    const metadata = snapshot.metadata[node.id] ?? { Support: 'Unsupported', Source: 'Unsupported' };
    const options = (metadata.Choices?.Options ?? []).map(option => ({ option_id: option.Id, label: option.Label, enabled: option.Enabled }));
    const selected = options.find(option => option.option_id === metadata.Choices?.SelectedId);
    return { ref: previous.get(key)?.ref ?? randomUUID(), key, name: node.name, role: node.role, type: fieldType(node.role),
        value: fieldType(node.role) === 'choice' ? selected?.label ?? null : node.value,
        visible: metadata.Visible ?? 'unknown', enabled: metadata.Enabled ?? node.enabled,
        read_only: metadata.ReadOnly ?? 'unknown', required: metadata.Required ?? 'unknown', source: metadata.Source,
        nativeId: node.id, target, metadata, options, optionsRev: metadata.Choices?.Revision ?? 'unknown' };
}

function publicField(field) {
    if (!field) return null;
    return { field_ref: field.ref, name: field.name, field_key: field.metadata.Field ?? field.name, role: field.role, type: field.type,
        value: field.value, visible: field.visible, enabled: field.enabled, read_only: field.read_only, required: field.required,
        metadata_source: field.source, options_rev: field.optionsRev, dependencies: field.metadata.Dependencies ?? 'unknown' };
}

function revisions(form) {
    return { form_ref: form.ref, context_rev: form.contextRev, structure_rev: form.structureRev };
}

function requireWritable(field, writing) {
    if (field.metadata.Support !== 'Available' || field.visible !== true || !field.enabled || writing && field.read_only === true) {
        throw new Error('unsupported_hidden_disabled_or_read_only_field');
    }
}

function prepareSet(field, args) {
    if (args.option_id === undefined) {
        if (field.type !== 'text') throw new Error('Literal text is not a choice selection.');
        return { tool: 'type_text', expected: args.value,
            arguments: { text: args.value, clear: true, field_context: field.metadata.Context ?? '' } };
    }
    if (field.type !== 'choice' || !field.metadata.Choices?.Complete || args.options_rev !== field.optionsRev) throw new Error('stale_options');
    const option = field.options.find(option => option.option_id === args.option_id && option.enabled);
    if (!option) throw new Error('Unknown or disabled observed option.');
    return { tool: 'click_element', expected: option.label, arguments: {
        choice_action: 'select', option_id: option.option_id, options_revision: field.optionsRev,
        ...(field.metadata.Context != null ? { field_context: field.metadata.Context } : {}) } };
}
