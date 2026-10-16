import { readOptionalOutput } from './learned-functions.mjs';
import { definitionHash } from './workflow-definition.mjs';
import { functionScope } from './compatibility.mjs';

// Workflow stages bind configured cartridge identities to fresh app instances and reuse the same gateway/native guards and ledger.
export class WorkflowAdapter {
    constructor(parent, configuration, factory) {
        this.parent = parent;
        this.configuration = configuration;
        this.gateways = new Map();
        for (const learning of configuration.applications ?? []) {
            const gateway = factory(learning);
            gateway.serverInfo = parent.serverInfo;
            this.gateways.set(learning.application.key + '/' + learning.application.version, gateway);
        }
    }

    gateway(application) {
        const gateway = this.gateways.get(application.key + '/' + application.version);
        if (!gateway) throw new Error('Workflow application cartridge is not configured.');
        gateway.serverInfo = this.parent.serverInfo;
        return gateway;
    }

    function(reference, application) {
        const gateway = this.gateway(application);
        gateway.learned.functions = gateway.learned.load();
        return gateway.learned.get(reference.name);
    }

    async refreshInfo(job) {
        const result = this.parent.requireSuccess(await this.parent.native('get_machine_capabilities'));
        if (result.capabilities?.nodeId !== this.configuration.authority.node) throw new Error('Workflow node authority changed.');
        const windows = this.parent.requireSuccess(await this.parent.native('list_windows')).windows;
        for (const [alias, application] of Object.entries(job.definition.applications)) {
            const candidates = windows.filter(window => (application.process_name === undefined || window.ProcessName === application.process_name)
                && (application.window === undefined || window.Kind === application.window));
            if (candidates.length !== 1 || !candidates[0].ProcessStartedAtUtc) throw new Error('Workflow application instance is absent or ambiguous.');
            const current = candidates[0];
            const previous = job.applications[alias];
            if (previous && (previous.pid !== current.ProcessId || previous.started !== current.ProcessStartedAtUtc
                || previous.handle !== current.WindowHandle)) throw new Error('Workflow target application instance was replaced.');
            job.applications[alias] = { pid: current.ProcessId, started: current.ProcessStartedAtUtc, handle: current.WindowHandle };
            await this.gateway(application).info();
        }
    }

    async prepare(stage, parameters, job) {
        await this.refreshInfo(job);
        await this.requireStableSources(stage, job);
        const application = job.definition.applications[stage.function_ref.application];
        const gateway = this.gateway(application);
        const entry = this.function(stage.function_ref, application);
        const pid = job.applications[stage.function_ref.application].pid;
        const before = await gateway.preflightFunction(entry, pid, await gateway.bindFunctionWindows(entry, pid));
        const outputs = Object.fromEntries(entry.outputs.map(output => [output.name,
            readOptionalOutput(before.get(output.selector.window) ?? [], output.selector, output.field)]));
        job.function_hash_kind ??= Object.keys(job.function_hashes ?? {}).length ? 'full-entry/1' : 'function-scope/1';
        const hash = checkpointHash(entry, job.function_hash_kind);
        if (job.function_hashes?.[stage.id] && job.function_hashes[stage.id] !== hash) throw new Error('Workflow function revision changed.');
        job.function_hashes ??= {};
        job.function_hashes[stage.id] = hash;
        const mutationSteps = entry.steps.flatMap((step, index) => step.action === 'read' ? [] : [index]);
        return { gateway, entry, pid, before, evidence: {
            outputs, function_hash: hash, function_hash_kind: job.function_hash_kind,
            expected_steps: entry.steps.length, mutation_steps: mutationSteps } };
    }

    async requireStableSources(stage, job) {
        const sources = [...new Set(Object.values(stage.bindings).filter(binding => binding.stage).map(binding => binding.stage))];
        for (const id of sources) {
            const source = job.definition.stages.find(stage => stage.id === id);
            const entry = this.function(source.function_ref, job.definition.applications[source.function_ref.application]);
            if (!entry.steps.every(step => step.action === 'read')) continue;
            const parameters = Object.fromEntries(Object.entries(source.bindings).map(([name, binding]) => [name,
                binding.input ? job.parameters[binding.input] : job.outputs[binding.stage][binding.output]]));
            const current = await this.currentOutputs(source, parameters, job.stage_evidence[id], job);
            if (definitionHash(current) !== job.output_digests[id]) throw new Error('Workflow document source digest changed before the target input.');
        }
    }

    async execute(stage, parameters, prepared, recorder, job) {
        const gateway = prepared.gateway;
        if (stage.decision) {
            if (job.parameters[stage.decision.input] !== true) throw new Error('Required workflow decision was not approved.');
            gateway.operationDecision = { severity: 'light', occasion: stage.decision.prompt, choice: 'Confirm',
                reason: 'Explicit typed workflow decision supplied by the authorized client.', expected_result: 'Verified recorded stage postconditions' };
        }
        gateway.operationRecorder = recorder;
        try {
            const response = await gateway.executeFunction(prepared.entry, { pid: prepared.pid, parameters });
            if (!response.success || !response.verified) throw new Error(response.error ?? 'Native stage is unconfirmed.');
            return response;
        } finally {
            gateway.operationRecorder = null;
            gateway.operationDecision = null;
        }
    }

    async currentOutputs(stage, parameters, before, job) {
        await this.refreshInfo(job);
        const application = job.definition.applications[stage.function_ref.application];
        const entry = this.function(stage.function_ref, application);
        if (checkpointHash(entry, before.function_hash_kind ?? 'full-entry/1') !== before.function_hash) {
            throw new Error('Checkpointed workflow function changed.');
        }
        const gateway = this.gateway(application);
        const pid = job.applications[stage.function_ref.application].pid;
        await gateway.requireApplicationInstance(pid);
        gateway.checkLearningIdentity(pid, entry);
        const nodes = (await gateway.readTree(pid, entry.steps.some(step => step.action === 'select'))).nodes;
        const verifiedBeforeStep = job.pending?.stage_id === stage.id ? job.pending.step_index ?? 0 : 0;
        if (job.pending?.stage_id === stage.id && entry.steps.some((step, index) =>
            (job.pending.operations ?? []).filter(operation => operation.step_index === index).length < expectedOperations(step))) {
            throw new Error('Only a prefix of the native stage was attempted; do not replay its remaining effects.');
        }
        requireFinalWrites(gateway, entry, parameters, nodes, verifiedBeforeStep);
        const values = {};
        for (const output of entry.outputs) {
            const actual = readOptionalOutput(nodes, output.selector, output.field);
            if (!actual.exists || typeof actual.value !== 'string') throw new Error('Workflow output is absent or unverified.');
            if (output.check.kind === 'equals_parameter' && actual.value !== parameters[output.check.parameter]
                || output.check.kind === 'changed' && actual.value === before.outputs[output.name]?.value
                || output.check.kind === 'not_empty' && !actual.value.trim()) throw new Error('Workflow postcondition is unconfirmed.');
            values[output.name] = actual.value;
        }
        return values;
    }

    async verify(stage, parameters, before, outputs, job) {
        const current = await this.currentOutputs(stage, parameters, before, job);
        return JSON.stringify(current) === JSON.stringify(outputs);
    }

    async reconcile(stage, parameters, before, job) {
        return this.currentOutputs(stage, parameters, before, job);
    }

    async operationStatus(requestId) {
        return this.parent.native('get_action_status', { request_id: requestId });
    }
}

function requireFinalWrites(gateway, entry, parameters, nodes, verifiedBeforeStep) {
    const writes = new Map();
    entry.steps.forEach((step, index) => {
        if (step.action === 'write') writes.set(JSON.stringify(step.selector), { step, index });
    });
    for (const { step, index } of writes.values()) {
        const actual = readOptionalOutput(nodes, step.selector, 'value');
        // Reaching a later checkpointed step proves the setter's full verification preceded the action that closed its field.
        if (!actual.exists && index < verifiedBeforeStep) continue;
        gateway.requireWrittenValue(nodes, step.selector, parameters[step.parameter]);
    }
}

function checkpointHash(entry, kind) {
    if (kind === 'function-scope/1') return functionScope(entry);
    if (kind === 'full-entry/1') return definitionHash(entry);
    throw new Error('Unknown workflow function checkpoint format.');
}

function expectedOperations(step) {
    if (step.action === 'read') return 0;
    return step.action === 'write' && step.route.startsWith('semantic-focus+keyboard') ? 4 : 1;
}
