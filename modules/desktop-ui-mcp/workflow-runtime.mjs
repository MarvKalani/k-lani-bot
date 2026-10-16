import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { Workflows } from './workflows.mjs';
import { WorkflowStore } from './workflow-store.mjs';
import { WorkflowAdapter } from './workflow-adapter.mjs';
import { validateWorkflow } from './workflow-definition.mjs';

export const workflowDefinitions = [
    descriptor('workflow.run', 'Run a verified workflow with stage-local preflight and durable native-operation intent. Never replay unknown effects.',
        { workflow_key: text(), revision: integer(), parameters: { type: 'object' }, client_request_id: text() }, ['workflow_key', 'revision', 'parameters', 'client_request_id']),
    descriptor('workflow.status', 'Read the private job status, verified stages and pending decision/error.', { job_ref: text() }, ['job_ref']),
    descriptor('workflow.resume', 'Refresh native Info and reconcile previous operation IDs with node ledger and current app state before continuing.',
        { job_ref: text(), expected_revision: integer(), answers: { type: 'object' } }, ['job_ref', 'expected_revision']),
    descriptor('Programming.workflow_capture', 'Compile ordered references to verified demonstrated functions into a workflow. '
        + 'Only parameter or confirmed-output bindings; no training values, PIDs, positions or JavaScript.',
        { definition: { type: 'object' } }, ['definition']),
    descriptor('Programming.workflow_begin', 'Begin collecting newly verified demonstrated functions as ordered stages. '
        + 'Navigation/dialog boundaries remain separate function demonstrations.',
        { key: text(), revision: integer(), inputs: { type: 'object' }, applications: { type: 'object' } }, ['key', 'revision', 'inputs', 'applications']),
    descriptor('Programming.workflow_finish', 'Finish captured stages with explicit typed bindings/outputs. Unverified actions never become executable stages.',
        { bindings: { type: 'object' }, decisions: { type: 'object' }, outputs: { type: 'object' } }, ['outputs'])
];

// The configured host gateway persists reusable definitions in their application/version directory and private jobs elsewhere.
export class WorkflowRuntime {
    constructor(parent, configuration, factory) {
        this.parent = parent;
        this.configuration = configuration;
        const state = path.resolve(configuration.stateRoot);
        this.cartridgeRoot = path.resolve(configuration.cartridgeRoot);
        if (state === this.cartridgeRoot || state.startsWith(this.cartridgeRoot + path.sep)
            || this.cartridgeRoot.startsWith(state + path.sep)) throw new Error('Workflow jobs and cartridge definitions need separate roots.');
        this.store = new WorkflowStore(state, configuration.authority);
        this.adapter = new WorkflowAdapter(parent, configuration, factory);
        this.definitions = new Map();
        this.load();
        this.engine = new Workflows(this.store, this.adapter, this.definitions);
        this.captureSession = null;
    }

    load() {
        this.definitions.clear();
        for (const learning of this.configuration.applications ?? []) {
            const application = learning.application;
            const file = path.join(this.cartridgeRoot, 'programs', application.key, application.version, 'workflows.json');
            if (!fs.existsSync(file)) continue;
            if (fs.lstatSync(file).isSymbolicLink() || fs.statSync(file).size > 1024 * 1024) throw new Error('Invalid workflow definition store.');
            const document = JSON.parse(fs.readFileSync(file, 'utf8'));
            if (document.format !== 'k-lani-bot-workflows/1' || document.application.key !== application.key
                || document.application.version !== application.version || !Array.isArray(document.workflows)) throw new Error('Workflow cartridge identity mismatch.');
            for (const definition of document.workflows) {
                validateWorkflow(definition, (reference, owner) => this.adapter.function(reference, owner));
                if (this.definitions.has(definition.key)) throw new Error('Duplicate workflow key.');
                if (this.definitions.size >= 100) throw new Error('Workflow definition catalog exceeds its bound.');
                this.definitions.set(definition.key, definition);
            }
        }
    }

    async call(name, args) {
        validateArguments(name, args);
        if (name === 'Programming.workflow_begin') {
            this.captureSession = { format: 'k-lani-bot-workflow/1', ...structuredClone(args), stages: [], outputs: {} };
            return { success: true, capturing: true };
        }
        if (name === 'Programming.workflow_finish') return this.finishCapture(args);
        if (name === 'Programming.workflow_capture') return this.capture(args.definition);
        if (name === 'workflow.status') return this.report(this.engine.status(args.job_ref));
        this.load();
        const status = name === 'workflow.run' ? await this.engine.run(args) : await this.engine.resume(args);
        return this.report(status);
    }

    recordCaptured(entry, application) {
        if (!this.captureSession) return;
        if (!entry.validation?.startsWith('demonstrated_once') || !entry.outputs?.length) throw new Error('Unverified actions remain candidates.');
        const match = Object.entries(this.captureSession.applications).filter(([, contract]) =>
            contract.key === application.key && contract.version === application.version);
        if (match.length !== 1) throw new Error('Captured application must have one declared workflow alias.');
        const required = entry.inputSchema.properties.parameters.required;
        const stage = { id: 'stage.' + (this.captureSession.stages.length + 1),
            function_ref: { application: match[0][0], name: entry.name },
            bindings: Object.fromEntries(required.filter(name => Object.hasOwn(this.captureSession.inputs, name))
                .map(name => [name, { input: name }])),
            preconditions: 'fresh_unique_enabled_targets', postconditions: 'recorded_function_outputs' };
        this.captureSession.stages.push(stage);
    }

    finishCapture(args) {
        if (!this.captureSession?.stages.length) throw new Error('No verified stages were captured.');
        const definition = structuredClone(this.captureSession);
        const identifiers = new Set(definition.stages.map(stage => stage.id));
        if ([...Object.keys(args.bindings ?? {}), ...Object.keys(args.decisions ?? {})].some(id => !identifiers.has(id))) {
            throw new Error('Capture bindings and decisions must name captured stages.');
        }
        for (const stage of definition.stages) {
            if (args.bindings?.[stage.id]) stage.bindings = structuredClone(args.bindings[stage.id]);
            if (args.decisions?.[stage.id]) stage.decision = structuredClone(args.decisions[stage.id]);
        }
        definition.outputs = structuredClone(args.outputs);
        const result = this.capture(definition);
        this.captureSession = null;
        return result;
    }

    capture(definition) {
        this.load();
        validateWorkflow(definition, (reference, application) => this.adapter.function(reference, application));
        if (this.definitions.has(definition.key)) throw new Error('Workflow already exists; revalidation must update its revision explicitly.');
        if (this.definitions.size >= 100) throw new Error('Workflow definition catalog exceeds its bound.');
        const primary = Object.values(definition.applications)[0];
        const file = path.join(this.cartridgeRoot, 'programs', primary.key, primary.version, 'workflows.json');
        if (fs.existsSync(file) && fs.lstatSync(file).isSymbolicLink()) throw new Error('Workflow definitions reject symbolic links.');
        const earlier = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : {
            format: 'k-lani-bot-workflows/1', application: { key: primary.key, version: primary.version }, workflows: [] };
        const document = { ...earlier, workflows: [...earlier.workflows, definition] };
        const text = JSON.stringify(document, null, 2) + '\n';
        if (Buffer.byteLength(text) > 1024 * 1024) throw new Error('Workflow definition store exceeds its bound.');
        fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
        const temporary = file + '.' + randomUUID() + '.tmp';
        fs.writeFileSync(temporary, text, { flag: 'wx', mode: 0o600 });
        fs.renameSync(temporary, file);
        this.definitions.set(definition.key, structuredClone(definition));
        return { success: true, workflow_key: definition.key, revision: definition.revision,
            stages: definition.stages.length, validation: 'verified function effects; fresh checks per stage', stored_values: false };
    }

    browse(args) {
        const descriptor = workflowDefinitions.find(definition => definition.name === args.function);
        if (descriptor) return { success: true, function: descriptor };
        this.load();
        return { success: true, topics: ['Use/Workflows'], functions: workflowDefinitions.filter(definition => definition.topic === 'Use/Workflows')
            .map(({ name, topic }) => ({ name, topic })),
            workflows: [...this.definitions.values()].slice(0, 100).map(({ key, revision, stages }) => ({ key, revision, stages: stages.length })),
            next_cursor: null };
    }

    report(status) {
        const job = this.store.read(status.job_ref);
        const evidence = this.parent.results.put(this.parent.resultNode, {}, JSON.stringify({
            completed_stages: job.completed_stages, outputs: job.outputs, pending: job.pending, error: job.error }));
        return { ...status, evidence_ref: evidence };
    }
}

function text() {
    return { type: 'string', maxLength: 128 };
}
function integer() {
    return { type: 'integer', minimum: 1 };
}
function descriptor(name, description, properties, required) {
    return { name, description, topic: name.startsWith('Programming.') ? 'Programming/Workflows' : 'Use/Workflows',
        inputSchema: { type: 'object', properties, required, additionalProperties: false } };
}

function validateArguments(name, args) {
    const schema = workflowDefinitions.find(definition => definition.name === name)?.inputSchema;
    if (!schema || !args || typeof args !== 'object' || Array.isArray(args)
        || Object.keys(args).some(key => !Object.hasOwn(schema.properties, key))
        || schema.required.some(key => !Object.hasOwn(args, key))) throw new Error('Invalid workflow arguments.');
    for (const [key, property] of Object.entries(schema.properties)) {
        if (args[key] === undefined) continue;
        if (property.type === 'string' && (typeof args[key] !== 'string' || args[key].length > 128)
            || property.type === 'integer' && (!Number.isInteger(args[key]) || args[key] < 1)
            || property.type === 'object' && (!args[key] || typeof args[key] !== 'object' || Array.isArray(args[key]))) throw new Error('Invalid workflow argument: ' + key);
    }
}
