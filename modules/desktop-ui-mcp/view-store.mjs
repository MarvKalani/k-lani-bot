import { randomUUID } from 'node:crypto';

export const viewLimits = Object.freeze({ contexts: 32, controls: 10000, ttl: 300000, page: 50, maximumPage: 200, completedViews: 2 });

// The same words as the compact C# gateway, so both twins tell the model how to reach omitted closed menus and hidden elements.
export const offscreenHint = 'Pass contains or offscreen:true to include closed menus and hidden elements.';

// A gateway retains completed baselines and opaque cursors per verified application/filter context, never input authority.
export class ViewStore {
    constructor(clock = Date.now) {
        this.clock = clock;
        this.contexts = new Map();
        this.cursors = new Map();
    }

    context(key) {
        this.expire();
        let context = this.contexts.get(key);
        if (!context) {
            while (this.contexts.size >= viewLimits.contexts) this.remove(this.contexts.keys().next().value);
            context = { key, ref: randomUUID(), identities: new Map(), targets: new Map(), records: [], baselines: new Map(),
                baseline: null, frame: null, forceFull: false };
        }
        this.contexts.delete(key);
        context.touched = this.clock();
        this.contexts.set(key, context);
        return context;
    }

    page(context, records, stamp, options) {
        validateViewOptions(options);
        if (options.cursor !== undefined) return this.continuePage(context, stamp, options);
        this.dropCursors(context.key);
        if (options.full || context.forceFull) {
            context.baselines.clear();
            context.baseline = null;
        }
        const reason = this.baselineReason(context, options);
        const baseline = reason ? null : context.baselines.get(options.since_view) ?? context.baseline;
        const includeOffscreen = options.includeOffscreen ?? true;
        const items = baseline ? deltaItems(baseline.records, records, !includeOffscreen) : fullItems(records, includeOffscreen);
        const offscreenOmitted = baseline || includeOffscreen ? 0 : records.filter(record => record.offscreen).length;
        context.records = records;
        context.frame = { ref: randomUUID(), stamp, items, kind: baseline ? 'delta' : 'full', reason, offset: 0, offscreenOmitted };
        this.enforceControls(context);
        return this.takePage(context, options.limit ?? viewLimits.page);
    }

    baselineReason(context, options) {
        if (options.full || context.forceFull) return 'reset';
        if (options.since_view !== undefined && !context.baselines.has(options.since_view)) {
            const foreign = [...this.contexts.values()].some(other => other.key !== context.key && other.baselines.has(options.since_view));
            return foreign ? 'context_mismatch' : 'unknown_or_incomplete_view';
        }
        return context.baseline ? null : 'no_completed_baseline';
    }

    continuePage(context, stamp, options) {
        const cursor = this.cursors.get(options.cursor);
        if (!cursor || cursor.key !== context.key || cursor.ref !== context.frame?.ref || stamp !== context.frame.stamp
            || options.full || options.since_view !== undefined || options.limit !== undefined && options.limit !== cursor.limit) {
            if (stamp !== context.frame?.stamp) this.dropCursors(context.key);
            throw new Error('stale_cursor');
        }
        this.cursors.delete(options.cursor);
        context.frame.offset = cursor.offset;
        return this.takePage(context, cursor.limit);
    }

    takePage(context, limit) {
        const frame = context.frame;
        const page = frame.items.slice(frame.offset, frame.offset + limit);
        frame.offset += page.length;
        const complete = frame.offset >= frame.items.length;
        let next_cursor = null;
        if (complete) {
            context.baseline = { ref: frame.ref, records: context.records };
            context.baselines.set(frame.ref, context.baseline);
            while (context.baselines.size > viewLimits.completedViews) context.baselines.delete(context.baselines.keys().next().value);
            context.forceFull = false;
        } else {
            next_cursor = randomUUID();
            this.cursors.set(next_cursor, { key: context.key, ref: frame.ref, offset: frame.offset, limit });
        }
        const result = frame.kind === 'full' ? { elements: page.map(item => item.record) }
            : { changed: page.filter(item => item.type === 'changed').map(item => item.record),
                removed: page.filter(item => item.type === 'removed').map(item => item.ref),
                visibility: page.filter(item => item.type === 'visibility').map(item => item.record) };
        return { ...result, view_ref: frame.ref, context_ref: context.ref, kind: frame.kind, next_cursor, full: frame.kind === 'full',
            total: context.records.length, truncated: !complete, ...(frame.reason ? { baseline_reason: frame.reason } : {}),
            ...(frame.offscreenOmitted > 0 ? { offscreen_omitted: frame.offscreenOmitted, hint: offscreenHint } : {}) };
    }

    enforceControls(current) {
        if (current.records.length > viewLimits.controls) {
            this.remove(current.key);
            throw new Error('view_scope_too_large');
        }
        while ([...this.contexts.values()].reduce((sum, context) => sum + retainedControls(context), 0) > viewLimits.controls) {
            const key = this.contexts.keys().next().value;
            if (key === current.key) {
                this.remove(key);
                throw new Error('view_scope_too_large');
            }
            this.remove(key);
        }
    }

    expire() {
        for (const [key, context] of this.contexts) if (this.clock() - context.touched >= viewLimits.ttl) this.remove(key);
    }

    dropCursors(key) {
        for (const [token, cursor] of this.cursors) if (cursor.key === key) this.cursors.delete(token);
    }

    remove(key) {
        this.dropCursors(key);
        this.contexts.delete(key);
    }

    clear() {
        this.contexts.clear();
        this.cursors.clear();
    }
}

export function validateViewOptions(options) {
    if (options.limit !== undefined && (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > viewLimits.maximumPage)
        || options.since_view !== undefined && typeof options.since_view !== 'string'
        || options.cursor !== undefined && typeof options.cursor !== 'string'
        || options.cursor !== undefined && (options.since_view !== undefined || options.full)) throw new Error('Invalid view page arguments.');
}

function retainedControls(context) {
    return new Set([...context.records, ...[...context.baselines.values()].flatMap(baseline => baseline.records)]
        .map(record => record.ref)).size;
}

// A full page lists its onscreen controls, or every control when offscreen rows were requested.
function fullItems(records, includeOffscreen) {
    return records.filter(record => includeOffscreen || !record.offscreen).map(record => ({ type: 'element', record }));
}

// A lean page never listed an offscreen control; one that appears is reported whole, so its reference becomes usable.
function deltaItems(before, after, revealAsChanged) {
    const previous = new Map(before.map(record => [record.ref, record]));
    const current = new Set(after.map(record => record.ref));
    const items = [];
    for (const record of after) {
        const earlier = previous.get(record.ref);
        const shownWhole = revealAsChanged && earlier !== undefined && Boolean(earlier.offscreen) && !record.offscreen;
        if (withoutVisibility(earlier) !== withoutVisibility(record) || shownWhole) items.push({ type: 'changed', record });
        if (earlier && Boolean(earlier.offscreen) !== Boolean(record.offscreen) && !shownWhole) {
            items.push({ type: 'visibility', record: { ref: record.ref, offscreen: Boolean(record.offscreen) } });
        }
    }
    for (const reference of previous.keys()) if (!current.has(reference)) items.push({ type: 'removed', ref: reference });
    return items;
}

function withoutVisibility(record) {
    if (!record) return undefined;
    const { offscreen, ...attributes } = record;
    return JSON.stringify(attributes);
}
