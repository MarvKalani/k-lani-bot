import path from 'node:path';
import { selectorCandidates, windowScope } from './projection.mjs';
import { formScanDefinition, formReadDefinition } from './form-scanner.mjs';
import { resultModeSchema, resultFieldsSchema } from './result-format.mjs';
import { resultDefinition } from './result-store.mjs';
import { ViewStore, validateViewOptions } from './view-store.mjs';
import { formDefinitions } from './form-contract.mjs';
import { CartridgeRevisions } from './cartridge-revisions.mjs';
import { validateCompatibility } from './compatibility.mjs';
import { compatibilityDefinitions } from './compatibility-runtime.mjs';

const identifier = /^[a-z][a-z0-9._-]{0,79}$/;
const categories = ['Administration', 'Use', 'Info', 'Programming'];

// Each window row names at most this many stored functions; the rest are counted, so the reply stays small.
export const listedFunctionLimit = 12;

// Every automatic name starts with this prefix, so listings tell a generated function from a named one without parsing its hash.
export function isGeneratedName(name) {
    return name.startsWith('ui.');
}

// Functions are sorted by name in code-unit order, the same order as the compact gateway's ordinal sort, and each one names its
// stored description, its parameters in the order the demonstration declared them, and its step labels. Only the first functions up to the limit are listed; the rest are counted.
// Generated names are left out of what the model reads; they stay reachable through cartridge_browse by topic and name.
export function summarizeListedFunctions(functions) {
    const named = functions.filter(entry => !isGeneratedName(entry.name));
    const sorted = named.sort((left, right) => (left.name < right.name ? -1 : left.name > right.name ? 1 : 0));
    const listed = sorted.slice(0, listedFunctionLimit).map(listedFunctionRow);
    return { functions: listed, more: Math.max(sorted.length - listedFunctionLimit, 0) };
}

// A function without a description or parameters leaves those keys out, so no row carries an empty value.
function listedFunctionRow(entry) {
    return { name: entry.name, ...(entry.description !== undefined ? { description: entry.description } : {}),
        ...(entry.parameters.length > 0 ? { parameters: [...entry.parameters] } : {}), steps: entry.steps.slice(0, listedStepLimit) };
}

// A stored function holds at most this many steps; the listing keeps the same bound so a row stays small.
export const listedStepLimit = 12;
// The longest stored description; the compact gateway uses the same bound and refusal.
export const descriptionLimit = 200;
export const descriptionRefusal = 'The description must be 1 to 200 characters without control characters.';

// A description is one short line a later model reads in ui_info; a control character could break that line or hide text.
export function validDescription(description) {
    return typeof description === 'string' && description.length >= 1 && description.length <= descriptionLimit
        && !/[\u0000-\u001f\u007f-\u009f]/.test(description);
}

// The label names the action and the target path from stored data only; a write never shows the text it wrote.
export function stepLabel(step) {
    const path = (step.selector.containerPath ?? []).map(container => (container.name ?? '').trim()).filter(name => name.length > 0);
    const name = (step.selector.name ?? '').trim();

    return [step.action, [...path, name.length > 0 ? name : step.selector.role].join(' > ')].join(' ');
}

// Stored functions belong to a selected process; a browse by name without one is refused with this sentence.
// The same words as the compact gateway; learning refuses a token that is missing, stale or names no current demonstration.
export const evidenceRefusal = 'The evidence token is missing or unknown; pass the evidence value from the latest successful ui_act reply.';
// The same words as the compact gateway; a stored function name that several applications list needs a pid or window.
export const severalApplicationsRefusal = 'Several applications list this function; pass pid or window from ui_info.';
export const selectProcessHint = 'Call ui_info first; each window row lists its stored functions with their parameters.';
// cartridge_info and a browse without a topic name where stored functions are listed; the compact C# gateway sends the same words.
export const storedFunctionsSentence = 'Listed per window in ui_info.';
// After a ui_info that listed no function, the model is sent to the state route; the compact C# gateway sends the same words.
export const noStoredFunctionsSentence = 'No stored functions for the current windows; '
    + 'read a window with ui_observe {pid, as: "state"} and change it with ui_apply.';

// What the latest ui_info showed of stored functions, as the compact C# gateway records it.
export const StoredFunctionDiscovery = Object.freeze({ notObserved: 'notObserved', noneListed: 'noneListed', listed: 'listed' });

// The pointer to stored functions that cartridge_info and cartridge_browse send for the latest ui_info.
export function storedFunctionsSentenceFor(discovery) {
    if (discovery === StoredFunctionDiscovery.listed) return storedFunctionsSentence;
    if (discovery === StoredFunctionDiscovery.noneListed) return noStoredFunctionsSentence;

    return selectProcessHint;
}

// The UI gateway stores one generated extension per configured application/version, outside public source by default.
// A successful demonstration supplies selectors and route evidence; typed values, screenshots, PIDs and native IDs are not saved.
export class LearnedFunctions {
    constructor(configuration) {
        const application = configuration.application;
        if (!application || !identifier.test(application.key) || !/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}$/.test(application.version)) {
            throw new Error('Learning requires a configured application identity and version. Unknown versions cannot be persisted.');
        }
        if (application.version === 'unknown' || !['native-version', 'controlled-test-fixture'].includes(application.identity_source)) {
            throw new Error('Application identity must come from native version metadata or a controlled test fixture.');
        }
        if (application.identity_source === 'native-version' && (!application.process_name || application.process_name === 'unknown')) {
            throw new Error('Native learning requires the observed application process name as well as its version.');
        }
        this.application = application;
        this.file = path.join(path.resolve(configuration.cartridgeRoot), 'programs', application.key, application.version, 'generated-ui.json');
        const state = configuration.revisionStateRoot ?? path.join(path.dirname(path.resolve(configuration.cartridgeRoot)), '.k-lani-cartridge-revisions');
        if (path.resolve(state) === path.resolve(configuration.cartridgeRoot)
            || path.resolve(state).startsWith(path.resolve(configuration.cartridgeRoot) + path.sep)) {
            throw new Error('Private revision state must remain outside cartridge exports.');
        }
        this.revisions = new CartridgeRevisions(this.file, state, document => this.validateDocument(document));
        this.candidates = [];
        this.discovery = new ViewStore(configuration.clock);
        this.functions = this.load();
    }

    load() {
        this.revisions.recover();
        const snapshot = this.revisions.read();
        this.generation = snapshot.revision;
        this.revision = snapshot.digest ?? 'empty';
        this.sourceVersion = snapshot.document?.version ?? 2;
        if (!snapshot.document) {
            this.revision = 'empty';
            return [];
        }
        const document = snapshot.document;
        this.candidates = document.candidates ?? [];
        return structuredClone(document.functions);
    }

    validateDocument(document) {
        if (document.format !== 'k-lani-bot-generated-ui-functions' || ![1, 2].includes(document.version)
            || document.application?.key !== this.application.key || document.application?.version !== this.application.version
            || !Array.isArray(document.functions)) throw new Error('Generated cartridge identity or format does not match.');
        for (const entry of document.functions) validateStoredFunction(entry);
        for (const candidate of document.candidates ?? []) {
            if (!identifier.test(candidate.name) || !['click', 'write'].includes(candidate.action)) throw new Error('Invalid generated candidate.');
            validateSelector(candidate.selector, false);
        }
        if (new Set(document.functions.map(entry => entry.name)).size !== document.functions.length) throw new Error('Duplicate generated function.');
    }

    info() {
        return { application: this.application, generated_functions: this.functions.length, unverified_candidates: this.candidates.length,
            revision: this.generation, source_version: this.sourceVersion,
            coverage: 'Recorded functions only; full application coverage is unknown.' };
    }

    // Stored functions and candidates are visible only while the gateway has selected a process that runs this program.
    browse(args, storedVisible = true) {
        validateBrowse(args);
        this.functions = this.load();
        const form = formDefinitions.find(entry => entry.name === args.function);
        if (form) return { function: form };
        if (args.function === 'Programming.learn') return { function: learnDefinition };
        if (args.function === 'Programming.learn_read') return { function: readDefinition };
        if (args.function === 'Info.preflight') return { function: preflightDefinition };
        if (args.function === 'Info.scan_form') return { function: formScanDefinition };
        if (args.function === 'Info.form') return { function: formReadDefinition };
        if (args.function === 'Info.result') return { function: resultDefinition };
        if (args.function) {
            if (!storedVisible) throw new Error(selectProcessHint);
            const entry = this.get(args.function);
            return { function: { name: entry.name, ...(entry.description !== undefined ? { description: entry.description } : {}),
                topic: entry.topic, steps: entry.steps.map(stepLabel), inputSchema: functionInputSchema(entry),
                compatibility: { node_version: entry.compatibility.node_version, platform: entry.compatibility.platform,
                    application_version: entry.compatibility.application?.build ?? entry.compatibility.application_version },
                validation: entry.validation } };
        }
        if (!args.topic) return { topics: categories };
        const stored = storedVisible ? this.functions : [];
        const selected = [...stored, ...formDefinitions, ...compatibilityDefinitions]
            .filter(entry => entry.topic === args.topic || entry.topic.startsWith(args.topic + '/'));
        const topics = [...new Set(selected.map(entry => entry.topic))];
        const page = this.discoveryPage(selected, args, storedVisible);
        return { topics, ...page, ...(args.topic === 'Programming' ? { generator: 'Programming.learn' } : {}),
            ...(args.topic === 'Info' ? { preflight: 'Info.preflight', scanner: 'Info.scan_form', form: 'Info.form', result: 'Info.result' } : {}) };
    }

    discoveryPage(selected, args, storedVisible) {
        const functions = selected.map(entry => ({ ref: 'function/' + entry.name, name: entry.name, topic: entry.topic, type: 'function' }));
        const inTopic = entry => entry.topic === args.topic || entry.topic.startsWith(args.topic + '/');
        const candidates = !storedVisible ? [] : this.candidates.filter(inTopic)
            .map(entry => ({ ref: 'candidate/' + entry.name, name: entry.name, type: 'candidate' }));
        const context = this.discovery.context(args.topic);
        const page = this.discovery.page(context, [...functions, ...candidates], this.revision, { ...args, full: args.cursor === undefined });
        return { functions: page.elements.filter(entry => entry.type === 'function').map(({ name, topic }) => ({ name, topic })),
            candidates: page.elements.filter(entry => entry.type === 'candidate').map(({ name }) => ({ name, status: 'needs_verification' })),
            next_cursor: page.next_cursor, truncated: page.truncated, revision: this.revision };
    }

    get(name) {
        const entry = this.functions.find(candidate => candidate.name === name);
        if (!entry) throw new Error('Unknown generated function. Browse the selected topic first.');
        return entry;
    }

    install(entry) {
        validateStoredFunction(entry);
        if (this.functions.some(candidate => candidate.name === entry.name)) throw new Error('Function already exists; it was not overwritten.');
        this.save([...this.functions, entry], this.candidates.filter(candidate => candidate.name !== entry.name));
    }

    saveCandidate(candidate) {
        validateSelector(candidate.selector, false);
        if (!identifier.test(candidate.name) || !['click', 'write'].includes(candidate.action)) throw new Error('Invalid candidate.');
        const earlier = this.candidates.find(entry => entry.name === candidate.name);
        if (earlier) return;
        this.save(this.functions, [...this.candidates, candidate]);
    }

    save(functions, candidates) {
        const document = { format: 'k-lani-bot-generated-ui-functions', version: 2, application: this.application, functions, candidates };
        const snapshot = this.revisions.replace(document, this.generation, this.revision === 'empty' ? null : this.revision);
        this.generation = snapshot.revision;
        this.revision = snapshot.digest;
        this.sourceVersion = 2;
        this.functions = snapshot.document.functions;
        this.candidates = snapshot.document.candidates;
    }
}

export function validateBrowse(args) {
    validateViewOptions(args);
    if (Object.keys(args).some(key => !['topic', 'function', 'cursor', 'limit', 'window', 'pid'].includes(key))
        || args.topic !== undefined && typeof args.topic !== 'string' || args.function !== undefined && typeof args.function !== 'string'
        || args.window !== undefined && !Number.isInteger(args.window) || args.pid !== undefined && !Number.isInteger(args.pid)
        || args.cursor !== undefined && (!args.topic || args.function)
        || args.function !== undefined && args.limit !== undefined) throw new Error('Invalid cartridge discovery arguments.');
}

// A function that declares no parameters is called with its pid alone; the parameters object is required only with declared inputs.
export function functionInputSchema(entry) {
    const declaresParameters = Object.keys(entry.inputSchema.properties.parameters.properties).length > 0;
    return { ...entry.inputSchema, required: declaresParameters ? ['pid', 'parameters'] : ['pid'],
        properties: { ...entry.inputSchema.properties,
            result_mode: resultModeSchema, fields: { ...resultFieldsSchema,
                items: { type: 'string', enum: entry.outputs.map(output => output.name) } } } };
}

function validateStoredFunction(entry) {
    if (!identifier.test(entry?.name) || entry.description !== undefined && !validDescription(entry.description)
        || !Array.isArray(entry.steps) || entry.steps.length < 1 || entry.steps.length > 12
        || !Array.isArray(entry.outputs) || entry.outputs.length < 1 || entry.outputs.length > 8 || !entry.compatibility?.node_version) {
        throw new Error('Invalid generated function definition.');
    }
    if (entry.compatibility.format) validateCompatibility(entry.compatibility);
    if (new Set(entry.outputs.map(output => output.name)).size !== entry.outputs.length) throw new Error('Duplicate generated output.');
    const parameters = entry.inputSchema?.properties?.parameters;
    if (!parameters || !Array.isArray(parameters.required) || !parameters.properties || parameters.additionalProperties !== false) {
        throw new Error('Invalid generated parameter schema.');
    }
    for (const [index, step] of entry.steps.entries()) {
        if (!['click', 'write', 'select', 'read'].includes(step.action) || !['semantic', 'native-text', 'native-choice', 'native-read',
            'semantic-focus+keyboard (native editable interface unavailable)'].includes(step.route)) throw new Error('Unsupported generated action or route.');
        validateSelector(step.selector, false);
        if (['write', 'select'].includes(step.action) && (!identifier.test(step.parameter) || !Object.hasOwn(parameters.properties, step.parameter))) {
            throw new Error('Generated write input is not parameterized.');
        }
        if (Object.hasOwn(step, 'enabledBeforeFirstAction') && !validFirstActionFlag(entry.steps, index)) {
            throw new Error('Unsupported generated action or route.');
        }
    }
    for (const output of entry.outputs) {
        validateSelector(output.selector, true);
        if (!identifier.test(output.name) || !['name', 'value'].includes(output.field)
            || !['changed', 'equals_parameter', 'exists', 'not_empty'].includes(output.check?.kind)) throw new Error('Invalid generated output check.');
        if (output.check.kind === 'equals_parameter' && !Object.hasOwn(parameters.properties, output.check.parameter)) {
            throw new Error('Output check refers to an unknown parameter.');
        }
    }
}

// Capture records the flag only as false on a mutation that an earlier mutation may enable; the C# loader applies the same rule.
function validFirstActionFlag(steps, index) {
    const flag = steps[index].enabledBeforeFirstAction;
    if (typeof flag !== 'boolean') return false;
    return flag || (steps[index].action !== 'read' && steps.slice(0, index).some(earlier => earlier.action !== 'read'));
}

// A stored selector names its window by kind only; the same words as the node's list_windows reply.
const windowKindWords = ['main', 'dialog'];

function validateSelector(selector, output) {
    if (!selector || typeof selector.role !== 'string' || !windowKindWords.includes(selector.window)
        || Object.keys(selector).some(key => !['role', 'name', 'autoId', 'containerPath', 'window', ...(output ? ['namePrefix'] : [])]
            .includes(key))) {
        throw new Error('Generated selectors require an owning window and contain no IDs or coordinates.');
    }
    if (typeof selector.name !== 'string' && !(output && typeof selector.namePrefix === 'string')) throw new Error('Generated selector is not semantic.');
    if (selector.containerPath !== undefined && (!Array.isArray(selector.containerPath) || selector.containerPath.length > 12
        || selector.containerPath.some(part => !part || typeof part.role !== 'string'
            || Object.keys(part).some(key => !['role', 'autoId', 'name'].includes(key))
            || typeof part.autoId !== 'string' && typeof part.name !== 'string'))) throw new Error('Invalid semantic container path.');
}

export const learnDefinition = {
    name: 'Programming.learn',
    description: 'Compile the last successful ui_act demonstration into a parameterized function. '
        + 'Provide its evidence token, function name/topic, names for every write input, and observed outputs. '
        + 'Outputs must show a changed effect. A name_prefix can identify a result with variable text. '
        + 'Only click/write steps are currently supported; no coordinates or literal input values are stored. '
        + 'An optional description of 1-200 characters says what the function does; ui_info lists it with the step labels.',
    inputSchema: { type: 'object', properties: {
        evidence: { type: 'string' }, name: { type: 'string', pattern: '^[a-z][a-z0-9._-]{0,79}$' },
        topic: { type: 'string' }, description: { type: 'string' }, parameters: { type: 'array', items: { type: 'string' }, maxItems: 12 },
        outputs: { type: 'array', minItems: 1, maxItems: 8, items: { type: 'object', properties: {
            name: { type: 'string' }, ref: { type: 'string' }, field: { type: 'string', enum: ['name', 'value'] }, name_prefix: { type: 'string' }
        }, required: ['name', 'ref', 'field'], additionalProperties: false } }
    }, required: ['evidence', 'name', 'topic', 'parameters', 'outputs'], additionalProperties: false }
};

export const preflightDefinition = {
    name: 'Info.preflight',
    description: 'Read-only check of a selected function: application instance/version and every input target in a fresh tree. '
        + 'Requires a unique target of the recorded role, using Automation ID or an exact name in the owning window. '
        + 'A target disabled before the first action is accepted only when an earlier recorded step enables it; '
        + 'every input target must be enabled right before its own input. '
        + 'Missing targets stop execution; split workflows whose controls appear only after earlier actions. '
        + 'Calls also perform this check automatically and re-resolve targets before each action.',
    inputSchema: { type: 'object', properties: { pid: { type: 'integer', minimum: 1 }, window: { type: 'integer' },
        function: { type: 'string' } }, required: ['function'], additionalProperties: false }
};

export const readDefinition = {
    name: 'Programming.learn_read', description: 'Record a fresh, observed nonempty output as a reusable read function. '
        + 'No input is dispatched, and current text/digests/PIDs are never stored in the function definition.',
    inputSchema: { type: 'object', properties: { pid: { type: 'integer', minimum: 1 }, window: { type: 'integer' },
        name: { type: 'string' }, topic: { type: 'string' }, ref: { type: 'string' }, field: { type: 'string', enum: ['name', 'value'] },
        output: { type: 'string' } },
        required: ['pid', 'name', 'topic', 'ref', 'field', 'output'], additionalProperties: false }
};

export function validateLearning(args, demonstration) {
    if (!demonstration || args.evidence !== demonstration.evidence) throw new Error(evidenceRefusal);
    if (!identifier.test(args.name) || !/^(Administration|Use|Info|Programming)\/[A-Za-z0-9._/-]{1,100}$/.test(args.topic)) {
        throw new Error(
            'Invalid function name or topic. The name must match [a-z][a-z0-9._-]{0,79}; the topic must be Category/topic with Category Administration, Use, Info or Programming, for example Use/calculator.');
    }
    if (Object.keys(args).some(key => !Object.hasOwn(learnDefinition.inputSchema.properties, key))) throw new Error('Unknown learning argument.');
    if (Object.hasOwn(args, 'description') && !validDescription(args.description)) throw new Error(descriptionRefusal);
    const writes = demonstration.steps.filter(step => step.action === 'write');
    if (!Array.isArray(args.parameters) || args.parameters.length !== writes.length
        || args.parameters.some(name => !identifier.test(name)) || new Set(args.parameters).size !== args.parameters.length) {
        throw new Error('Name every write input exactly once. Literal training values are never persisted.');
    }
    if (demonstration.steps.some(step => !['click', 'write'].includes(step.action))) throw new Error('Standalone key sequences require an explicit focus recipe.');
    if (!Array.isArray(args.outputs) || args.outputs.length < 1 || args.outputs.length > 8) throw new Error('Supply 1–8 observed outputs.');
    const names = new Set();
    for (const output of args.outputs) {
        if (!output || !identifier.test(output.name) || names.has(output.name) || typeof output.ref !== 'string'
            || !['name', 'value'].includes(output.field) || Object.keys(output).some(key => !['name', 'ref', 'field', 'name_prefix'].includes(key))) {
            throw new Error('Invalid or duplicate output.');
        }
        if (output.name_prefix !== undefined && (typeof output.name_prefix !== 'string' || output.name_prefix.length < 4
            || output.name_prefix.length > 256)) throw new Error('Use a bounded literal result prefix.');
        names.add(output.name);
    }
}

export function readOutput(nodes, selector, field) {
    const result = readOptionalOutput(nodes, selector, field);
    if (!result.exists) throw new Error('Output is absent; completion is unverified.');
    return result.value;
}

// An output is read for capture evidence, so an ambiguous window is a capture failure rather than an input refusal.
function outputScope(nodes) {
    try {
        return windowScope(nodes);
    }
    catch {
        throw new Error('Window identity is ambiguous.');
    }
}

export function readOptionalOutput(nodes, selector, field) {
    const candidates = selectorCandidates(outputScope(nodes), selector, true);
    if (candidates.length > 1) throw new Error('Output is ambiguous; completion is unverified.');
    return { exists: candidates.length === 1, value: candidates[0]?.[field] ?? null };
}
