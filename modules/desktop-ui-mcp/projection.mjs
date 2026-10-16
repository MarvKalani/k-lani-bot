import { createHash } from 'node:crypto';
import { ViewStore, validateViewOptions } from './view-store.mjs';
import { roleVocabularyEntry } from './role-vocabulary.mjs';

// Native observations stay on the host. The model receives meaningful controls and text with stable local references.
// References are not coordinates or native IDs; the gateway resolves their observed identity before each action.
const linePattern = /^(\s*)\[(\d+)\] (.+?) ("(?:[^"\\]|\\.)*") \(autoId: ("(?:[^"\\]|\\.)*"), val: (null|"(?:[^"\\]|\\.)*"), enabled: (True|False), offscreen: (True|False)\)$/;
const editableRole = /\bEdit\b|entry/i;
const interactiveRole = /button|\bEdit\b|entry|tab|menu item|list item|check|radio|combo|slider|spin|link/i;

export function parseNativeTree(tree) {
    return tree.split('\n').filter(line => line.trim()).map(line => {
        const match = linePattern.exec(line);
        if (!match) throw new Error('Unsupported native tree format; no element references were issued.');
        return { depth: match[1].length, id: Number(match[2]), role: match[3], name: JSON.parse(match[4]),
            autoId: JSON.parse(match[5]), value: JSON.parse(match[6]), enabled: match[7] === 'True', offscreen: match[8] === 'True' };
    });
}

export function applicationBranch(nodes, rootName) {
    const dialogs = nodes.filter(node => /^(dialog|file chooser)$/i.test(node.role) && !node.offscreen);
    if (dialogs.length) return nodes.filter(node => dialogs.some(dialog => node === dialog || containsNode(nodes, dialog, node)));
    const documents = nodes.filter(node => node.role === 'document web' && node.name);
    const roots = documents.filter(node => !documents.some(parent => parent !== node && parent.depth < node.depth
        && containsNode(nodes, parent, node)));
    const candidates = rootName ? roots.filter(node => node.name === rootName) : roots;
    if (candidates.length > 1 || rootName && candidates.length !== 1) {
        throw new Error('Application root is ambiguous or absent. Observed document names: ' + roots.map(node => node.name).join(', '));
    }
    if (candidates.length === 0) return nodes;
    const root = candidates[0];
    const start = nodes.indexOf(root);
    let end = start + 1;
    while (end < nodes.length && nodes[end].depth > root.depth) end++;
    return nodes.slice(start, end);
}

function containsNode(nodes, parent, candidate) {
    const start = nodes.indexOf(parent);
    const target = nodes.indexOf(candidate);
    return target > start && nodes.slice(start + 1, target + 1).every(node => node.depth > parent.depth);
}

function isWindowRole(role) {
    return /^(Window|frame|dialog|file chooser)$/i.test(role);
}

// A tree read for one native window has one outermost window row; rows nested under it, such as a UWP frame's inner CoreWindow with
// the same caption, belong to it. A tree without a window row is used whole. Several outermost rows name no single window.
export function windowScope(nodes) {
    const windows = nodes.filter(node => isWindowRole(node.role));
    if (!windows.length) return nodes;
    const outermostDepth = Math.min(...windows.map(node => node.depth));
    const outermost = windows.filter(node => node.depth === outermostDepth);
    if (outermost.length !== 1) throw new Error('The observed window identity changed or is ambiguous. No input was sent.');
    return nodes.filter(node => node === outermost[0] || containsNode(nodes, outermost[0], node));
}

// An empty container path names a direct child of the window only when a window row contains the element.
export function hasWindowAncestor(nodes, node) {
    return nodes.some(window => isWindowRole(window.role) && containsNode(nodes, window, node));
}

// Input dispatch uses observedTarget, which refuses a disabled match before any native call.
export function observedTarget(nodes, target, stableIdentity = false) {
    const candidate = presentTarget(nodes, target, stableIdentity);
    if (!candidate.enabled) throw new Error('Target is disabled. No input was sent.');
    return candidate.id;
}

// Preflight uses presentTarget for a step that an earlier recorded step enables; the caller decides how a disabled match is treated.
export function presentTarget(nodes, target, stableIdentity = false) {
    const candidates = selectorCandidates(windowScope(nodes), target, stableIdentity);
    if (candidates.length !== 1) throw new Error('Target identity is absent or ambiguous. No input was sent.');
    return candidates[0];
}

// Learned selectors tolerate caption changes only with an observed Automation ID and the recorded role in their owning window.
export function selectorCandidates(nodes, selector, stableIdentity = false) {
    const identified = nodes.filter(node => (!selector.autoId || node.autoId === selector.autoId)
        && (selector.containerPath === undefined || JSON.stringify(semanticTarget(0, nodes, node).containerPath)
            === JSON.stringify(selector.containerPath)));
    if (stableIdentity && selector.autoId && identified.length && !identified.some(node => node.role === selector.role)) {
        throw new Error('Target type changed for the recorded Automation ID.');
    }
    return identified.filter(node => node.role === selector.role && matchesCaption(node, selector, stableIdentity));
}

function matchesCaption(node, selector, stableIdentity) {
    if (selector.namePrefix) return node.name.startsWith(selector.namePrefix);
    if ((stableIdentity || selector.containerPath !== undefined) && selector.autoId) return true;
    return node.name === selector.name;
}

// The UI gateway gives the model compact changes and keeps the observed identities needed for later semantic actions.
export class UiProjection {
    constructor(configuration = {}) {
        this.views = new ViewStore(configuration.clock);
        this.node = configuration.node ?? 'local-session';
        this.references = new Map();
        this.nextReference = 1;
    }

    get identities() {
        return new Map([...this.views.contexts.values()].flatMap(context => [...context.identities]));
    }

    observe(pid, nodes, options = {}) {
        validateViewOptions(options);
        const includeOffscreen = options.offscreen ?? true;
        const key = JSON.stringify({ node: options.node ?? this.node, pid, instance: options.instance ?? 'projection-fixture',
            root: options.root, offscreen: includeOffscreen, contains: options.contains,
            max_chars: options.max_chars ?? 2000 });
        const context = this.views.context(key);
        context.pid = pid;
        const selected = applicationBranch(nodes, options.root);
        const projection = this.projectRecords(pid, nodes, selected, options, context);
        const stamp = createHash('sha256').update(JSON.stringify(selected.map(({ id, ...node }) => node))).digest('hex');
        const result = this.views.page(context, projection.records, stamp, { ...options, includeOffscreen });
        context.targets = projection.targets;
        for (const element of result.elements ?? result.changed ?? []) {
            this.references.set(element.ref, { ...projection.targets.get(element.ref), context: key });
        }
        this.pruneReferences();
        return result;
    }

    projectRecords(pid, nodes, selected, options, context) {
        const targets = new Map();
        const records = [];
        const occurrences = new Map();
        const active = new Map();
        for (const node of selected) {
            if (!isProjected(nodes, node, options)) continue;
            const target = semanticTarget(pid, nodes, node, options.window);
            const identity = JSON.stringify(stableSelector(target));
            const occurrence = occurrences.get(identity) ?? 0;
            occurrences.set(identity, occurrence + 1);
            const key = identity + ':' + occurrence;
            const ref = context.identities.get(key) ?? 'e' + this.nextReference++;
            active.set(key, ref);
            targets.set(ref, target);
            records.push(compactElement(ref, node, usefulValue(node.value, node.role), options.max_chars ?? 2000));
        }
        for (const target of targets.values()) target.ambiguous = occurrences.get(JSON.stringify(stableSelector(target))) > 1;
        context.identities = active;
        return { records, targets };
    }

    target(ref, pid) {
        this.views.expire();
        this.pruneReferences();
        const exposed = this.references.get(ref);
        const target = exposed && this.views.contexts.get(exposed.context)?.targets.get(ref);
        if (!target || target.pid !== pid) throw new Error('Unknown reference for this application. Observe it first.');
        if (target.ambiguous) throw new Error('ambiguous_target: observed identity is ambiguous.');
        if (target.unverifiedWindow) throw new Error('unverified_window: native tree scope cannot be bound to one observed window.');
        return target;
    }

    pruneReferences() {
        for (const [ref, target] of this.references) {
            const context = this.views.contexts.get(target.context);
            if (!context || !context.targets.has(ref)) this.references.delete(ref);
        }
    }

    hideUnseen(visibleReferences, pid) {
        this.references = visibleReferences;
        for (const context of this.views.contexts.values()) {
            if ([...context.targets.values()].some(target => target.pid === pid)) {
                context.forceFull = true;
                this.views.dropCursors(context.key);
            }
        }
    }

    resetApplication(pid) {
        for (const [key, context] of this.views.contexts) if (context.pid === pid) this.views.remove(key);
        this.pruneReferences();
    }

    clear() {
        this.views.clear();
        this.references.clear();
    }
}

export function stableSelector(target) {
    const verifiedWindow = target.windowHandle !== undefined && /^(Window|frame|dialog|file chooser)$/i.test(target.role);
    return { role: target.role, ...(target.autoId ? { autoId: target.autoId } : verifiedWindow ? {} : { name: target.name }),
        containerPath: target.containerPath ?? [],
        ...(target.windowHandle === undefined ? { window: target.windowKind } : { windowHandle: target.windowHandle }) };
}

// The observed window (native list_windows row) owns every row of the tree it was read for; its caption is never matched.
export function semanticTarget(pid, nodes, node, observed) {
    const parents = nodes.filter(parent => containsNode(nodes, parent, node));
    const window = parents.find(parent => isWindowRole(parent.role)) ?? (isWindowRole(node.role) ? node : null);
    const windowIndex = parents.indexOf(window);
    const containers = window === node ? [] : windowIndex >= 0 ? parents.slice(windowIndex + 1)
        : parents.filter(parent => parent.role !== 'Root');
    return { pid, name: node.name, role: node.role, autoId: node.autoId,
        ...(observed ? { windowKind: observed.Kind } : {}),
        containerPath: containers.map(parent => ({ role: parent.role, ...(parent.autoId ? { autoId: parent.autoId } : { name: parent.name }) })),
        ...(observed ? { windowHandle: observed.WindowHandle, processStarted: observed.ProcessStartedAtUtc } : {}),
        unverifiedWindow: !observed };
}

// Offscreen controls stay projected: a full page decides whether to list them, while deltas must still report their changes.
function isProjected(nodes, node, options) {
    const value = usefulValue(node.value, node.role);
    if (!isInteractive(node.role) && !node.name && !value) return false;
    if (options.contains && !matchesText(node, value, options.contains)) return false;
    return !nodes.some(parent => containsNode(nodes, parent, node)
        && (parent.name || usefulValue(parent.value, parent.role)) === node.name && node.name && !isInteractive(node.role));
}

// A name or a value matches; each is compared on its own, so a search cannot match across their boundary.
function matchesText(node, value, text) {
    const needle = text.toLowerCase();
    return node.name.toLowerCase().includes(needle) || (value ?? '').toLowerCase().includes(needle);
}

// A platform vocabulary entry decides first; other role names keep the broad patterns the native gateway uses as well.
export function roleFamily(role) {
    const entry = roleVocabularyEntry(role);
    if (entry) return entry.family;
    if (editableRole.test(role)) return 'Editable';
    return interactiveRole.test(role) ? 'Interactive' : 'Other';
}

function isInteractive(role) {
    return roleFamily(role) !== 'Other';
}

function usefulValue(value, role) {
    if (typeof value !== 'string') return undefined;
    if (roleFamily(role) === 'Editable') return value;
    if (value.includes('\uFFFC') && !value.replaceAll('\uFFFC', '').trim()) return undefined;
    return value.replaceAll('\uFFFC', '');
}

function compactElement(ref, node, value, maximumCharacters) {
    const element = { ref, role: node.role, name: node.name.slice(0, maximumCharacters) };
    if (node.name.length > maximumCharacters) element.name_truncated = true;
    if (value !== undefined && (value !== node.name || roleFamily(node.role) === 'Editable')) {
        element.value = value.slice(0, maximumCharacters);
        if (value.length > maximumCharacters) element.value_truncated = true;
    }
    if (!node.enabled) element.disabled = true;
    if (node.offscreen) element.offscreen = true;
    return element;
}
