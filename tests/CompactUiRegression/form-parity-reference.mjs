import { FormRuntime } from '../../modules/desktop-ui-mcp/form-runtime.mjs';
import { ResultStore } from '../../modules/desktop-ui-mcp/result-store.mjs';
import { semanticTarget } from '../../modules/desktop-ui-mcp/projection.mjs';

// This host-only reference uses shared synthetic transport snapshots, never hidden answers from a real application.
export async function formFixtures(corpus) {
    const output = [];
    for (const fixture of corpus.cases) {
        let snapshotIndex = 0;
        let mutations = 0;
        let step;
        const snapshot = () => fixture.snapshots[snapshotIndex];
        const windows = () => [{ Title: snapshot().nodes[0].name, WindowHandle: 1, Kind: 'main',
            ProcessStartedAtUtc: snapshot().started ?? 'start' }];
        const scope = () => semanticTarget(10, snapshot().nodes, snapshot().nodes[0], windows()[0]);
        const gateway = {
            projection: { target: scope }, results: new ResultStore(), resultNode: 'fixture',
            requireApplicationInstance: async () => {},
            observeWindows: async () => ({ windows: windows() }),
            currentWindowTarget: async (_pid, target) => {
                if (target.processStarted !== windows()[0].ProcessStartedAtUtc) {
                    throw new Error('The observed window instance was replaced or cannot be verified.');
                }
                return { ...target, windowKind: windows()[0].Kind };
            },
            readTree: async () => ({ ...snapshot(), snapshot_id: 'fixture-snapshot' }),
            native: async () => {
                mutations++;
                snapshotIndex = step.after_snapshot ?? snapshotIndex;
                return { success: true };
            },
            requireSuccess: result => { if (!result.success) throw new Error(result.error); }
        };
        const runtime = new FormRuntime(gateway);
        const replies = new Map();
        const references = new Map();
        const steps = [];
        let form;
        for (step of fixture.steps) {
            snapshotIndex = step.snapshot ?? snapshotIndex;
            try {
                const args = step.action === 'read' ? { pid: 10, scope: 'observed' }
                    : { form_ref: form.ref, context_rev: step.context_from ? replies.get(step.context_from).context_rev : form.contextRev };
                if (step.field) args.field_ref = form.fields.find(field => field.name === step.field).ref;
                if (step.fields) args.fields = step.fields;
                if (step.limit !== undefined) args.limit = step.limit;
                if (step.cursor_from) args.cursor = replies.get(step.cursor_from).next_cursor;
                if (step.value !== undefined) args.value = step.value;
                if (step.option_id !== undefined) {
                    args.option_id = step.option_id;
                    args.options_rev = step.options_rev ?? form.fields.find(field => field.name === step.field).optionsRev;
                }
                const reply = await runtime.call('form.' + step.action, args);
                form = [...runtime.forms.values()][0];
                references.set(form.ref, '$form');
                for (const field of form.fields) {
                    if (!references.has(field.ref)) references.set(field.ref, '$field' + (references.size - 1));
                }
                replies.set(step.name, reply);
                steps.push({ name: step.name, reply: normalize(reply, references) });
            } catch (error) {
                steps.push({ name: step.name, reply: { error: error.message } });
            }
        }
        output.push({ name: fixture.name, steps, mutations });
    }
    return output;
}

function normalize(value, references, key) {
    if (Array.isArray(value)) return value.map(item => normalize(item, references));
    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([name, item]) => [name, normalize(item, references, name)]));
    }
    if (typeof value === 'string') {
        if (key === 'evidence_ref') return '$evidence';
        if (key === 'next_cursor') return '$cursor';
        if (key === 'section') return 'outputs/' + references.get(value.slice('outputs/'.length));
        return references.get(value) ?? value;
    }
    return value;
}
