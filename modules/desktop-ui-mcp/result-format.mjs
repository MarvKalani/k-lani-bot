import { resultLimits } from './result-store.mjs';

export const resultModeSchema = { type: 'string', enum: ['summary', 'values', 'debug'] };
export const resultFieldsSchema = { type: 'array', items: { type: 'string' }, maxItems: 20, uniqueItems: true };

export function validateResultOptions(args, outputNames = null) {
    if (args.result_mode !== undefined && !resultModeSchema.enum.includes(args.result_mode)) throw new Error('Invalid result_mode.');
    if (args.fields === undefined) return;
    if (args.result_mode !== 'values' || !outputNames || !Array.isArray(args.fields) || args.fields.length > 20
        || new Set(args.fields).size !== args.fields.length
        || args.fields.some(name => typeof name !== 'string' || !outputNames.includes(name))) throw new Error('Invalid result fields.');
}

// Verified application data is retained locally before any optional model-facing projection is shortened.
export function formatResult(store, node, result, args, checks = {}, scan = null) {
    if (args.result_mode === undefined) return result;
    const outputs = result.outputs ?? {};
    const debug = JSON.stringify(result);
    let evidence_ref;
    try {
        evidence_ref = store.put(node, outputs, debug);
    } catch (error) {
        if (error.message !== 'evidence_too_large') throw error;
        return { ...baseResult(result), success: false, error: error.message, observation_required: Boolean(result.ui),
            retry: 'Do not replay completed input. Request a narrower result scope.' };
    }
    if (args.result_mode === 'debug') {
        const inline = { ...result, evidence_ref };
        if (Buffer.byteLength(JSON.stringify(inline)) <= resultLimits.debugBytes) return inline;
        return debugIndex(result, evidence_ref, Object.keys(outputs), scan);
    }
    const compact = summaryResult(result, checks, scan);
    if (args.result_mode === 'values') compact.outputs = valuesOf(outputs, args.fields, checks, evidence_ref);
    return { ...compact, evidence_ref };
}

function debugIndex(result, evidence_ref, names, scan) {
    const keys = ['success', 'verified', 'function', 'completed_steps', 'steps', 'failed_step', 'failed_step_may_have_changed_ui',
        'phase', 'input_sent', 'retry', 'unconfirmed_effects', 'form', 'state', 'observed_states',
        'unknown', 'action_status', 'offscreen_meaning', 'coverage'];
    const index = Object.fromEntries(keys.filter(key => Object.hasOwn(result, key)).map(key => [key, result[key]]));
    if (result.error) index.error = Array.from(result.error).slice(0, 512).join('');
    if (scan) index.field_count = scan.field_count;
    if (result.change) index.change_counts = Object.fromEntries(Object.entries(result.change).map(([name, items]) => [name, items.length]));
    return { ...index, evidence_ref, truncated: true, ...(result.ui ? { observation_required: true } : {}),
        sections: ['debug', ...names.slice(0, 20).map(name => 'outputs/' + name)], total_sections: names.length + 1 };
}

function baseResult(result) {
    const omitted = new Set(['outputs', 'ui', 'fields', 'contents', 'images']);
    return Object.fromEntries(Object.entries(result).filter(([key]) => !omitted.has(key)));
}

function summaryResult(result, checks, scan) {
    const summary = baseResult(result);
    if (summary.error) summary.error = Array.from(summary.error).slice(0, 2000).join('');
    if (result.ui) keepSmallChangeList(result.ui, summary);
    if (scan) Object.assign(summary, { field_count: scan.field_count, content_count: scan.content_count });
    if (result.outputs) summary.outputs = Object.fromEntries(Object.entries(result.outputs).map(([name, text]) =>
        [name, { name, ...checks[name], utf8_length: Buffer.byteLength(text) }]));
    return summary;
}

const maximumInlineChangeEntries = 20;

// A small, known change list answers the question the model would otherwise re-read the window for; only a large one is replaced
// by counts and an observation request.
function keepSmallChangeList(ui, summary) {
    const count = name => Array.isArray(ui[name]) ? ui[name].length : 0;
    const counts = { changed: count('changed'), removed: count('removed'), visibility: count('visibility') };
    if (counts.changed + counts.removed + counts.visibility <= maximumInlineChangeEntries) {
        summary.ui = ui;
        return;
    }
    Object.assign(summary, { ui_counts: counts, observation_required: true, observation_reason: 'changes_too_large' });
}

function valuesOf(outputs, fields, checks, evidence_ref) {
    const selected = fields ?? Object.keys(outputs);
    return Object.fromEntries(selected.filter(name => Object.hasOwn(outputs, name)).map(name => {
        const text = outputs[name];
        const characters = Array.from(text);
        const truncated = characters.length > resultLimits.page;
        return [name, { name, text: characters.slice(0, resultLimits.page).join(''), ...checks[name],
            utf8_length: Buffer.byteLength(text), truncated,
            ...(truncated ? { evidence_ref, section: 'outputs/' + name } : {}) }];
    }));
}
