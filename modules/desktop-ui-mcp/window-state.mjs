// Called by the gateway's ui_observe state view and by the shared parity check to describe one native window as a bounded, typed class.
import { createHash } from 'node:crypto';

const NAMESPACE_LIMIT = 20;
const ROW_LIMIT = 20;
const DIFF_LIMIT = 20;
const DEFAULT_MAX_CHARS = 2000;
const LETTER_OR_DIGIT = /[\p{L}\p{Nd}]/u;
const DIGIT_START = /\p{Nd}/u;
const NUMBER_TEXT = /^-?[0-9]+(?:\.[0-9]+)?$/u;
const OFFSET_TEXT = /^[0-9]+$/u;
const WINDOW_ROLE = /^(?:window|frame|dialog|file chooser)$/iu;
const SEPARATOR_ROLE = /separator/iu;
const PASSWORD_ROLE = /password/iu;
const TEXT_ROLE = /(?<![\p{L}\p{N}_])Edit(?![\p{L}\p{N}_])|entry/iu;
const NUMBER_ROLE = /spin|slider/iu;
const CHOICE_ROLE = /combo|popupbutton/iu;
const COLLECTION_ROLE = /^(?:list|list box|table|tree|tree table|outline)$/iu;
const MENU_BAR_ROLE = /menu bar|menubar/iu;
const MENU_ROLE = /menu/iu;
const TOGGLE_ROLE = /check|radio|toggle/iu;
const METHOD_ROLE = /button|menu ?item|link|^menu$|tab/iu;
const CAPTIONED_METHOD_ROLE = /button|menu ?item|link/iu;
const LAYOUT_FRAME_ROLE = /panel|filler|pane|group|scroll|split|viewport|section|layered/iu;
const LABEL_ROLE = /^(?:label|static|statictext|text)$/iu;
const ROW_ROLE = /item|cell|row/iu;

const ElementKind = Object.freeze({
    ignore: 'ignore',
    secret: 'secret',
    text: 'text',
    number: 'number',
    choice: 'choice',
    collection: 'collection',
    namespace: 'namespace',
    container: 'container',
    toggle: 'toggle',
    method: 'method',
    label: 'label',
    unmapped: 'unmapped',
});
const MemberKind = Object.freeze({ property: 'prop', collection: 'coll', method: 'method', namespace: 'ns' });
const FALLBACK_NAMES = Object.freeze({ text: 'text', secret: 'password', number: 'number', choice: 'choice' });
const SCALAR_KINDS = [ElementKind.text, ElementKind.secret, ElementKind.number, ElementKind.choice];

// Returns { value } for a readable window, or { error } when the tree has no window or the cursor names no part.
export function buildWindowState(nodes, { windowHandle, kind, maxChars = DEFAULT_MAX_CHARS, cursor = null }) {
    const windowIndex = nodes.findIndex(node => WINDOW_ROLE.test(node.role));
    if (windowIndex < 0) return { error: 'no_window' };
    const core = buildCore(nodes, windowIndex, windowHandle, kind);
    const title = nodes[windowIndex].name ?? null;
    if (cursor === null) return { value: wholeWindowValue(core, windowHandle, kind, title, maxChars) };
    return cursorValue(core, windowHandle, kind, maxChars, cursor);
}

// Maps every declared member path to the native row it stands for, so a later action acts on exactly that row.
export function bindWindowState(nodes) {
    const windowIndex = nodes.findIndex(node => WINDOW_ROLE.test(node.role));
    if (windowIndex < 0) return { error: 'no_window' };
    return { value: walkWindow(nodes, windowIndex).bindings };
}

// Compares two complete window states; unchanged parts are omitted, so an empty result means no change at all.
export function diffWindowStates(beforeNodes, afterNodes, { windowHandle, kind, maxChars = DEFAULT_MAX_CHARS }) {
    const beforeIndex = beforeNodes.findIndex(node => WINDOW_ROLE.test(node.role));
    const afterIndex = afterNodes.findIndex(node => WINDOW_ROLE.test(node.role));
    if (beforeIndex < 0 || afterIndex < 0) return { error: 'no_window' };
    const before = buildCore(beforeNodes, beforeIndex, windowHandle, kind);
    const after = buildCore(afterNodes, afterIndex, windowHandle, kind);
    const result = {};
    addValueChanges(result, before, after, maxChars);
    addEnabledChanges(result, before.bindings, after.bindings);
    capped(result, 'added', pathsMissingFrom(after.bindings, before.bindings));
    capped(result, 'removed', pathsMissingFrom(before.bindings, after.bindings));
    addTitleChange(result, beforeNodes[beforeIndex].name, afterNodes[afterIndex].name);
    addShapeChange(result, before.shape, after.shape);
    return { value: result };
}

function addValueChanges(result, before, after, maxChars) {
    const values = {};
    for (const path of [...before.state.keys(), ...pathsMissingFrom(after.state, before.state)]) {
        const oldValue = before.state.has(path) ? before.state.get(path) : null;
        const newValue = after.state.has(path) ? after.state.get(path) : null;
        const presenceChanged = before.state.has(path) !== after.state.has(path);
        if (presenceChanged || !sameValue(oldValue, newValue)) {
            values[path] = [diffSide(oldValue, maxChars), diffSide(newValue, maxChars)];
        }
    }
    if (Object.keys(values).length > 0) result.values = values;
}

// A collection side is a digest of its rows; a long text side is clipped like the state view.
function diffSide(value, maxChars) {
    if (Array.isArray(value)) return { rows: value.length, sha256: sha256Prefix(value.join('\n')) };
    return clipValue(value, maxChars);
}

function sameValue(left, right) {
    if (!Array.isArray(left) || !Array.isArray(right)) return left === right;
    return left.length === right.length && left.every((row, index) => row === right[index]);
}

function addEnabledChanges(result, beforeBindings, afterBindings) {
    const enabled = {};
    for (const [path, afterBinding] of afterBindings) {
        const beforeBinding = beforeBindings.get(path);
        if (beforeBinding !== undefined && beforeBinding.enabled !== afterBinding.enabled) {
            enabled[path] = afterBinding.enabled;
        }
    }
    if (Object.keys(enabled).length > 0) result.enabled = enabled;
}

function pathsMissingFrom(source, other) {
    return [...source.keys()].filter(path => !other.has(path));
}

// Lists at most DIFF_LIMIT paths and counts the rest, so one large change cannot flood the diff.
function capped(result, key, paths) {
    if (paths.length === 0) return;
    result[key] = paths.slice(0, DIFF_LIMIT);
    if (paths.length > DIFF_LIMIT) result[key + '_more'] = paths.length - DIFF_LIMIT;
}

function addTitleChange(result, beforeTitle, afterTitle) {
    if (beforeTitle !== afterTitle) result.title = [beforeTitle, afterTitle];
}

function addShapeChange(result, beforeShape, afterShape) {
    if (beforeShape !== afterShape) result.shape = { before: beforeShape, after: afterShape };
}

// One braces block of the declaration; keys stay unique among its own members.
class Namespace {
    constructor() {
        this.members = [];
        this.keys = new Set();
    }

    addKey(baseText, fallback) {
        const identifierText = identifier(baseText);
        const quoted = identifierText === null && baseText !== '';
        let base = identifierText;
        if (identifierText === null) base = quoted ? baseText : fallback;
        let candidate = base;
        let suffix = 1;
        while (this.keys.has(candidate)) {
            suffix += 1;
            candidate = base + String(suffix);
        }
        this.keys.add(candidate);
        return { key: candidate, quoted };
    }

    leafCount() {
        let total = 0;
        for (const member of this.members) {
            total += member.kind === MemberKind.namespace ? member.payload.leafCount() : 1;
        }
        return total;
    }
}

// Walks the window body once, in tree order, and collects members, state, labels and unmapped rows.
class WindowWalker {
    constructor(body) {
        this.body = body;
        this.root = new Namespace();
        this.labels = [];
        this.unmapped = new Map();
        this.hiddenOmitted = 0;
        this.state = new Map();
        this.bindings = new Map();
    }

    walk(start, end, namespace, path, inNamespace) {
        let index = start;
        while (index < end) {
            index = this.visit(index, namespace, path, inNamespace);
        }
    }

    visit(index, namespace, path, inNamespace) {
        const node = this.body[index];
        const stop = subtreeEnd(this.body, index);
        if (node.offscreen && !inNamespace) {
            this.hiddenOmitted += stop - index;
            return stop;
        }
        const kind = classifyNode(node, stop > index + 1);
        this.visitKind(kind, index, stop, namespace, path, inNamespace);
        return stop;
    }

    visitKind(kind, index, stop, namespace, path, inNamespace) {
        const node = this.body[index];
        if (SCALAR_KINDS.includes(kind)) this.addScalar(kind, node, namespace, path);
        else if (kind === ElementKind.collection) this.addCollection(index, stop, namespace, path);
        else if (kind === ElementKind.namespace) this.addNamespace(index, stop, namespace, path);
        else if (kind === ElementKind.container) this.walk(index + 1, stop, namespace, path, inNamespace);
        else if (kind === ElementKind.method || kind === ElementKind.toggle) this.addMethod(kind, node, namespace, path, index, stop);
        else if (kind === ElementKind.label) this.addLabel(node);
        else if (kind === ElementKind.unmapped) this.countUnmapped(node.role, trim(node.name));
    }

    // Records which native row a declared member stands for, so a later action acts on exactly that row.
    bind(statePath, node, memberKind, fallback) {
        this.bindings.set(statePath, { id: node.id, member: memberKind, enabled: node.enabled, fallback });
    }

    addScalar(kind, node, namespace, path) {
        const text = sourceText(node.name);
        const { key, quoted } = namespace.addKey(text, FALLBACK_NAMES[kind]);
        const notes = disabledNotes(node);
        if (kind === ElementKind.secret) notes.push('write-only');
        if (kind === ElementKind.choice) notes.push('choice');
        const payload = kind === ElementKind.number ? 'number' : 'string';
        namespace.members.push(member(MemberKind.property, key, quoted, notes, payload));
        const statePath = joinPath([...path, { key, quoted }]);
        this.bind(statePath, node, kind, text === '');
        this.recordScalarState(kind, node, statePath);
    }

    recordScalarState(kind, node, statePath) {
        if (kind === ElementKind.secret) return;
        const numeric = kind === ElementKind.number && node.value !== null ? numericValue(node.value) : null;
        this.state.set(statePath, numeric ?? node.value);
    }

    addCollection(index, stop, namespace, path) {
        const node = this.body[index];
        const text = sourceText(node.name);
        const { key, quoted } = namespace.addKey(text, 'items');
        namespace.members.push(member(MemberKind.collection, key, quoted, disabledNotes(node), null));
        const statePath = joinPath([...path, { key, quoted }]);
        this.bind(statePath, node, ElementKind.collection, text === '');
        this.state.set(statePath, this.collectRows(index + 1, stop));
    }

    collectRows(start, stop) {
        return this.body.slice(start, stop)
            .filter(row => ROW_ROLE.test(row.role))
            .map(row => trim(row.name) || (row.value ?? ''));
    }

    addNamespace(index, stop, namespace, path) {
        const node = this.body[index];
        const text = sourceText(node.name);
        if (text === '') {
            this.countUnmapped(node.role, trim(node.name));
            return;
        }
        const { key, quoted } = namespace.addKey(text, 'menu');
        const child = new Namespace();
        const statePath = joinPath([...path, { key, quoted }]);
        this.bind(statePath, node, ElementKind.namespace, false);
        this.walk(index + 1, stop, child, [...path, { key, quoted }], true);
        if (child.members.length > 0) {
            namespace.members.push(member(MemberKind.namespace, key, quoted, disabledNotes(node), child));
        } else {
            namespace.keys.delete(key);
            this.bindings.delete(statePath);
        }
    }

    addMethod(kind, node, namespace, path, index, stop) {
        let text = sourceText(node.name);
        if (text === '') text = this.captionFromDescendants(index, stop);
        if (text === '') {
            this.countUnmapped(node.role, '');
            return;
        }
        const notes = disabledNotes(node);
        if (kind === ElementKind.toggle) notes.push('toggle');
        const { key, quoted } = namespace.addKey(text, null);
        namespace.members.push(member(MemberKind.method, key, quoted, notes, null));
        this.bind(joinPath([...path, { key, quoted }]), node, kind, false);
    }

    // A button without its own name is captioned by its first labelled descendant, the text a user reads on it.
    captionFromDescendants(index, stop) {
        for (let row = index + 1; row < stop; row += 1) {
            const candidate = this.body[row];
            if (!LABEL_ROLE.test(candidate.role)) continue;
            const caption = sourceText(trim(candidate.value ?? '') || candidate.name);
            if (caption !== '') return caption;
        }
        return '';
    }

    addLabel(node) {
        const text = trim(node.value ?? '') || trim(node.name);
        if (text !== '') this.labels.push(text);
    }

    countUnmapped(role, name) {
        const key = JSON.stringify([role, name]);
        if (!this.unmapped.has(key)) this.unmapped.set(key, { role, name, count: 0 });
        const entry = this.unmapped.get(key);
        entry.count += 1;
    }

    addLabelsMember() {
        if (this.labels.length === 0) return;
        const { key, quoted } = this.root.addKey('texts', 'texts');
        this.root.members.push(member(MemberKind.collection, key, quoted, [], null));
        this.state.set(key, this.labels);
    }
}

// Walks the window body once; the walker keeps members, state and the bindings of declared members.
function walkWindow(nodes, windowIndex) {
    const body = windowBody(nodes, windowIndex);
    const walker = new WindowWalker(body);
    walker.walk(0, body.length, walker.root, [], false);
    walker.addLabelsMember();
    return walker;
}

function buildCore(nodes, windowIndex, windowHandle, kind) {
    const walker = walkWindow(nodes, windowIndex);
    const declaration = coreDeclaration(walker.root, windowHandle, kind);
    return {
        root: walker.root,
        state: walker.state,
        unmapped: [...walker.unmapped.values()],
        hiddenOmitted: walker.hiddenOmitted,
        bindings: walker.bindings,
        shape: sha256Prefix(shapeText(declaration)),
    };
}

// The declaration without member notes: a control turning enabled or disabled changes state, not shape.
function shapeText(declaration) {
    const lines = declaration.split('\n');
    return [lines[0], ...lines.slice(1).map(line => line.split(' // ')[0])].join('\n');
}

function windowBody(nodes, windowIndex) {
    const windowDepth = nodes[windowIndex].depth;
    const body = [];
    for (let index = windowIndex + 1; index < nodes.length && nodes[index].depth > windowDepth; index++) {
        body.push(nodes[index]);
    }
    return body;
}

function subtreeEnd(body, index) {
    const depth = body[index].depth;
    let end = index + 1;
    while (end < body.length && body[end].depth > depth) end += 1;
    return end;
}

// A leaf frame without a name or value is structure only; a labelled frame is classified like any other leaf.
function isEmptyLayoutFrame(node) {
    return LAYOUT_FRAME_ROLE.test(node.role) && trim(node.name) === '' && trim(node.value ?? '') === '';
}

function classifyNode(node, hasChildren) {
    const role = node.role;
    if (SEPARATOR_ROLE.test(role)) return ElementKind.ignore;
    if (PASSWORD_ROLE.test(role) || role.toLowerCase() === 'securetextfield') return ElementKind.secret;
    if (TEXT_ROLE.test(role) || ['textfield', 'textarea'].includes(role.toLowerCase())) return ElementKind.text;
    if (NUMBER_ROLE.test(role)) return ElementKind.number;
    if (CHOICE_ROLE.test(role)) return ElementKind.choice;
    if (COLLECTION_ROLE.test(role)) return ElementKind.collection;
    if (MENU_BAR_ROLE.test(role)) return ElementKind.container;
    if (MENU_ROLE.test(role) && hasChildren) return ElementKind.namespace;
    // A native button or check box often keeps its caption as a child label, so its role decides before its children.
    if (hasChildren && TOGGLE_ROLE.test(role)) return ElementKind.toggle;
    if (hasChildren && CAPTIONED_METHOD_ROLE.test(role)) return ElementKind.method;
    if (hasChildren) return ElementKind.container;
    if (isEmptyLayoutFrame(node)) return ElementKind.ignore;
    if (TOGGLE_ROLE.test(role)) return ElementKind.toggle;
    if (METHOD_ROLE.test(role)) return ElementKind.method;
    if (LABEL_ROLE.test(role)) return ElementKind.label;
    return ElementKind.unmapped;
}

function member(kind, key, quoted, notes, payload) {
    return { kind, key, quoted, notes, payload };
}

function disabledNotes(node) {
    return node.enabled ? [] : ['disabled'];
}

function numericValue(text) {
    const trimmed = trim(text);
    return NUMBER_TEXT.test(trimmed) ? Number(trimmed) : null;
}

function coreDeclaration(root, windowHandle, kind) {
    const lines = [headerLine(windowHandle, kind, null)];
    renderNamespace(root, 1, lines, [], 1, null);
    lines.push('}');
    return lines.join('\n');
}

// The title is evidence only (an unsaved marker, for one); it is never part of the declaration or the shape.
function wholeWindowValue(core, windowHandle, kind, title, maxChars) {
    const view = newView(core.state);
    const lines = [headerLine(windowHandle, kind, null)];
    renderNamespace(core.root, 1, lines, [], 1, view);
    lines.push('}');
    const value = { window: windowHandle, kind, title, declaration: lines.join('\n'), state: pageState(view, maxChars) };
    addMore(value, view);
    value.unmapped = core.unmapped;
    value.hidden_omitted = core.hiddenOmitted;
    value.shape = core.shape;
    return value;
}

function cursorValue(core, windowHandle, kind, maxChars, cursor) {
    const { target, offsetText } = splitCursor(cursor);
    const found = OFFSET_TEXT.test(offsetText) ? findPart(core.root, [], target) : null;
    if (!isReadablePart(found, offsetText)) return { error: 'unknown_part' };
    if (found.kind === MemberKind.collection) {
        return { value: collectionPageValue(core, windowHandle, kind, maxChars, target, Number(offsetText)) };
    }
    return { value: namespacePartValue(core, windowHandle, kind, maxChars, found, target) };
}

function isReadablePart(found, offsetText) {
    if (found === null) return false;
    if (found.kind === MemberKind.collection) return true;
    return found.kind === MemberKind.namespace && offsetText === '0';
}

function splitCursor(cursor) {
    const separatorIndex = cursor.lastIndexOf('@');
    if (separatorIndex < 0) return { target: '', offsetText: cursor };
    return { target: cursor.slice(0, separatorIndex), offsetText: cursor.slice(separatorIndex + 1) };
}

function findPart(namespace, path, target) {
    for (const candidate of namespace.members) {
        const parts = [...path, { key: candidate.key, quoted: candidate.quoted }];
        if (joinPath(parts) === target) return { kind: candidate.kind, parts, payload: candidate.payload };
        if (candidate.kind === MemberKind.namespace) {
            const found = findPart(candidate.payload, parts, target);
            if (found !== null) return found;
        }
    }
    return null;
}

function collectionPageValue(core, windowHandle, kind, maxChars, target, offset) {
    const rows = core.state.get(target);
    const value = {
        window: windowHandle,
        kind,
        part: target,
        rows: rows.slice(offset, offset + ROW_LIMIT).map(row => clipValue(row, maxChars)),
        total: rows.length,
    };
    if (offset + ROW_LIMIT < rows.length) value.more = { [target]: `${target}@${offset + ROW_LIMIT}` };
    value.shape = core.shape;
    return value;
}

function namespacePartValue(core, windowHandle, kind, maxChars, found, target) {
    const view = newView(core.state);
    const lines = [headerLine(windowHandle, kind, target)];
    let level = 1;
    for (const part of found.parts) {
        lines.push(`${'  '.repeat(level)}${partName(part)}: {`);
        level += 1;
    }
    renderNamespace(found.payload, level, lines, found.parts, 1, view);
    for (let back = level - 1; back > 0; back -= 1) lines.push(`${'  '.repeat(back)}};`);
    lines.push('}');
    const value = { window: windowHandle, kind, part: target, declaration: lines.join('\n'), state: pageState(view, maxChars) };
    addMore(value, view);
    value.shape = core.shape;
    return value;
}

function partName(part) {
    return part.quoted ? quote(part.key) : part.key;
}

function headerLine(windowHandle, kind, partText) {
    const className = kind === 'main' ? 'Main' : 'Dialog';
    const part = partText === null ? '' : `, part ${partText}`;
    return `class ${className} { // window ${windowHandle}, kind ${kind}${part}`;
}

function newView(fullState) {
    return { fullState, more: new Map(), visible: [] };
}

function renderNamespace(namespace, level, lines, path, relative, view) {
    for (const candidate of namespace.members) {
        renderMember(candidate, level, lines, path, relative, view);
    }
}

function renderMember(candidate, level, lines, path, relative, view) {
    const pad = '  '.repeat(level);
    const name = partName(candidate);
    const memberParts = [...path, { key: candidate.key, quoted: candidate.quoted }];
    const memberPath = joinPath(memberParts);
    const notes = memberNotes(candidate, memberPath, view);
    const tail = notes.length > 0 ? ` // ${notes.join(', ')}` : '';
    if (candidate.kind === MemberKind.property) {
        lines.push(`${pad}${name}: ${candidate.payload};${tail}`);
        recordVisible(view, memberPath);
    } else if (candidate.kind === MemberKind.collection) {
        lines.push(`${pad}readonly ${name}: string[];${tail}`);
        recordVisible(view, memberPath);
    } else if (candidate.kind === MemberKind.method) {
        lines.push(`${pad}${name}(): void;${tail}`);
    } else if (isCollapsed(candidate, relative, view)) {
        lines.push(`${pad}${name}: { /* ${candidate.payload.leafCount()} members */ };${tail}`);
        view.more.set(memberPath, `${memberPath}@0`);
    } else {
        lines.push(`${pad}${name}: {${tail}`);
        renderNamespace(candidate.payload, level + 1, lines, memberParts, relative + 1, view);
        lines.push(`${pad}};`);
    }
}

function memberNotes(candidate, memberPath, view) {
    const notes = [...candidate.notes];
    if (candidate.kind !== MemberKind.collection || view === null) return notes;
    const rows = view.fullState.get(memberPath) ?? [];
    if (rows.length > ROW_LIMIT) {
        notes.push(`${rows.length} rows`);
        view.more.set(memberPath, `${memberPath}@${ROW_LIMIT}`);
    }
    return notes;
}

function isCollapsed(candidate, relative, view) {
    return view !== null && relative >= 2 && candidate.payload.leafCount() > NAMESPACE_LIMIT;
}

function recordVisible(view, memberPath) {
    if (view !== null) view.visible.push(memberPath);
}

function pageState(view, maxChars) {
    const entries = [];
    for (const memberPath of view.visible) {
        if (view.fullState.has(memberPath)) {
            entries.push([memberPath, pageValue(view.fullState.get(memberPath), maxChars)]);
        }
    }
    return Object.fromEntries(entries);
}

function pageValue(value, maxChars) {
    if (Array.isArray(value)) return value.slice(0, ROW_LIMIT).map(row => clipValue(row, maxChars));
    return clipValue(value, maxChars);
}

function addMore(value, view) {
    if (view.more.size > 0) value.more = Object.fromEntries(view.more);
}

function clipValue(value, maxChars) {
    if (typeof value !== 'string') return value;
    const characters = [...value];
    if (characters.length <= maxChars) return value;
    return {
        start: characters.slice(0, maxChars).join(''),
        length: characters.length,
        sha256: sha256Prefix(value),
    };
}

function identifier(text) {
    const words = splitWords(text);
    if (words.length === 0) return null;
    const result = words[0].toLowerCase() + words.slice(1).map(capitalizeWord).join('');
    if (DIGIT_START.test([...result][0])) return null;
    return result;
}

function splitWords(text) {
    const words = [];
    let current = '';
    for (const character of text) {
        if (LETTER_OR_DIGIT.test(character)) {
            current += character;
        } else if (current !== '') {
            words.push(current);
            current = '';
        }
    }
    if (current !== '') words.push(current);
    return words;
}

function capitalizeWord(word) {
    const characters = [...word];
    return characters[0].toUpperCase() + characters.slice(1).join('').toLowerCase();
}

function sourceText(name) {
    let text = trim(name);
    for (const suffix of ['...', '…']) {
        if (text.endsWith(suffix)) text = trimEnd(text.slice(0, text.length - suffix.length));
    }
    return text;
}

// The same set as .NET char.IsWhiteSpace, so both gateways cut names alike.
const TRIMMED_WHITESPACE = new Set([
    0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680,
    0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a,
    0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
]);

function trim(text) {
    return trimEnd(trimStart(text));
}

function trimStart(text) {
    const characters = [...text];
    let start = 0;
    while (start < characters.length && isTrimmedWhitespace(characters[start])) start += 1;
    return characters.slice(start).join('');
}

function trimEnd(text) {
    const characters = [...text];
    let end = characters.length;
    while (end > 0 && isTrimmedWhitespace(characters[end - 1])) end -= 1;
    return characters.slice(0, end).join('');
}

function isTrimmedWhitespace(character) {
    return TRIMMED_WHITESPACE.has(character.codePointAt(0));
}

function joinPath(parts) {
    let output = '';
    for (const part of parts) {
        if (part.quoted) {
            output += `[${quote(part.key)}]`;
        } else {
            output += `${output === '' ? '' : '.'}${part.key}`;
        }
    }
    return output;
}

function quote(text) {
    return `"${text.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

function sha256Prefix(text) {
    return createHash('sha256').update(text, 'utf8').digest('hex').slice(0, 16);
}
