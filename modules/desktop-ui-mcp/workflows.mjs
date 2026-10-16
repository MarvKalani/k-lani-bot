import { randomUUID } from 'node:crypto';
import { validateWorkflow, validateParameters, resolveBindings, definitionHash } from './workflow-definition.mjs';

export const workflowStatuses = Object.freeze(['running', 'awaiting_input', 'blocked', 'unconfirmed', 'failed', 'completed']);

// Stages use the existing node operation ledger through an adapter; private checkpoints retain intent, response and verified outputs.
export class Workflows {
    constructor(store, adapter, definitions) {
        this.store = store;
        this.adapter = adapter;
        this.definitions = definitions;
    }

    async run(args) {
        const definition = this.definitions.get(args.workflow_key);
        if (!definition || definition.revision !== args.revision) throw new Error('Unknown workflow revision.');
        validateWorkflow(definition, (reference, application) => this.adapter.function(reference, application));
        validateParameters(definition.inputs, args.parameters, true);
        const hash = definitionHash({ definition, parameters: args.parameters });
        const job = this.store.create(args.client_request_id, hash, definition, args.parameters);
        return this.store.serialized(job.job_ref, current => this.advance(current));
    }

    status(reference) {
        return statusOf(this.store.read(reference));
    }

    async resume(args) {
        return this.store.serialized(args.job_ref, async job => {
            if (job.revision !== args.expected_revision) throw new Error('stale_job_revision');
            validateParameters(job.definition.inputs, args.answers ?? {}, true);
            if (Object.keys(args.answers ?? {}).some(name => Object.hasOwn(job.parameters, name)
                && !(job.status === 'awaiting_input' && job.decision?.input === name
                    && job.parameters[name] === false && args.answers[name] === true
                    && !inputWasConsumed(job, name)))) {
                throw new Error('Confirmed workflow inputs cannot be replaced on resume.');
            }
            Object.assign(job.parameters, args.answers ?? {});
            this.store.write(job);
            try {
                await this.adapter.refreshInfo(job);
            } catch (error) {
                return this.mark(job, job.pending ? 'unconfirmed' : 'blocked', error.message);
            }
            if (job.pending) {
                const reconciled = await this.reconcile(job);
                if (!reconciled) return statusOf(job);
            }
            return this.advance(job);
        });
    }

    async advance(job) {
        if (job.status === 'completed') return statusOf(job);
        if (job.pending) return this.mark(job, permissionBlocked(job.pending.operations) ? 'blocked' : 'unconfirmed',
            'Reconcile the previous native operation before continuing.');
        job.status = 'running';
        try {
            await this.adapter.refreshInfo(job);
        } catch (error) {
            return this.mark(job, 'blocked', error.message);
        }
        for (const stage of job.definition.stages) {
            if (job.completed_stages.includes(stage.id)) continue;
            const missing = missingInput(stage, job);
            if (missing) {
                job.decision = { input: missing, prompt: stage.decision?.prompt ?? 'Supply the declared workflow input.' };
                return this.mark(job, 'awaiting_input');
            }
            delete job.decision;
            const result = await this.executeStage(job, stage);
            if (!result) return statusOf(job);
        }
        job.final_outputs = resolveBindings(job.definition.outputs, job.parameters, job.outputs);
        return this.mark(job, 'completed');
    }

    async executeStage(job, stage) {
        const parameters = resolveBindings(stage.bindings, job.parameters, job.outputs);
        let prepared;
        try {
            prepared = await this.adapter.prepare(stage, parameters, job);
        } catch (error) {
            this.mark(job, /scope|permission|instance|target|absent|ambiguous/i.test(error.message) ? 'blocked' : 'failed', error.message);
            return false;
        }
        job.pending = { stage_id: stage.id, phase: 'prepared', before: prepared.evidence, operations: [], parameters };
        this.store.write(job);
        try {
            const recorder = this.recorder(job);
            const result = await this.adapter.execute(stage, parameters, prepared, recorder, job);
            if (!result.verified || !await this.adapter.verify(stage, parameters, prepared.evidence, result.outputs, job)) {
                this.mark(job, 'unconfirmed', 'Stage effect did not satisfy its fresh postconditions.');
                return false;
            }
            job.pending.phase = 'verified';
            job.pending.outputs = result.outputs;
            this.store.write(job);
            this.completeStage(job, stage.id, result.outputs);
            return true;
        } catch (error) {
            this.mark(job, permissionBlocked(job.pending.operations) ? 'blocked' : 'unconfirmed', error.message);
            return false;
        }
    }

    recorder(job) {
        return {
            step: index => {
                job.pending.step_index = index;
                this.store.write(job);
            },
            before: (tool, args) => {
                const request_id = operationId();
                const operation = { request_id, tool, step_index: job.pending.step_index, arguments: { ...args, request_id }, phase: 'intent' };
                job.pending.operations.push(operation);
                this.store.write(job);
                operation.phase = 'dispatching';
                job.pending.phase = 'dispatching';
                this.store.write(job);
                return operation.arguments;
            },
            after: (requestId, response) => {
                const operation = job.pending.operations.find(operation => operation.request_id === requestId);
                if (!operation) throw new Error('Unknown checkpointed native operation.');
                operation.response = structuredClone(response);
                operation.phase = 'responded';
                this.store.write(job);
            }
        };
    }

    async reconcile(job) {
        const pending = job.pending;
        const stage = job.definition.stages.find(stage => stage.id === pending.stage_id);
        if (pending.phase === 'prepared' && pending.operations.length === 0) {
            job.pending = null;
            this.store.write(job);
            return true;
        }
        try {
            if (pending.phase !== 'verified' && pending.before.expected_steps !== undefined
                && (pending.step_index !== pending.before.expected_steps - 1
                    || pending.before.mutation_steps?.some(index => !pending.operations.some(operation => operation.step_index === index)))) {
                this.mark(job, 'unconfirmed', 'Only a prefix of the stage was attempted; its remaining effects cannot be inferred.');
                return false;
            }
            for (const operation of pending.operations) {
                const result = await this.adapter.operationStatus(operation.request_id, job);
                if (result.outcome === 'not_dispatched' && ['denied', 'consent_denied'].includes(result.code)) {
                    operation.response = result;
                    this.mark(job, permissionBlocked(pending.operations) ? 'blocked' : 'unconfirmed', 'Native scope or consent refused the operation.');
                    return false;
                }
                if (!result.success || !['completed', 'dispatched', 'verified'].includes(result.outcome)) {
                    this.mark(job, 'unconfirmed', 'Native ledger cannot establish the previous effect.');
                    return false;
                }
            }
            const outputs = await this.adapter.reconcile(stage, pending.parameters, pending.before, job);
            if (!outputs || !await this.adapter.verify(stage, pending.parameters, pending.before, outputs, job)) {
                this.mark(job, 'unconfirmed', 'Current application state does not establish the checkpointed effect.');
                return false;
            }
            this.completeStage(job, stage.id, outputs);
            return true;
        } catch (error) {
            this.mark(job, 'unconfirmed', error.message);
            return false;
        }
    }

    completeStage(job, stageId, outputs) {
        job.outputs[stageId] = structuredClone(outputs);
        job.stage_evidence ??= {};
        job.stage_evidence[stageId] = structuredClone(job.pending.before);
        job.output_digests ??= {};
        job.output_digests[stageId] = definitionHash(outputs);
        job.completed_stages.push(stageId);
        job.pending = null;
        job.status = 'running';
        delete job.error;
        this.store.write(job);
    }

    mark(job, status, error = null) {
        if (!workflowStatuses.includes(status)) throw new Error('Invalid workflow status.');
        job.status = status;
        if (error) job.error = error;
        this.store.write(job);
        return statusOf(job);
    }
}

function missingInput(stage, job) {
    if (stage.decision && job.parameters[stage.decision.input] !== true) return stage.decision.input;
    return Object.values(stage.bindings).find(binding => binding.input && !Object.hasOwn(job.parameters, binding.input))?.input;
}

function permissionBlocked(operations) {
    return operations.length > 0 && operations.every(operation => operation.response?.outcome === 'not_dispatched'
        && ['denied', 'consent_denied'].includes(operation.response.code));
}

function inputWasConsumed(job, name) {
    return job.definition.stages.some(stage => job.completed_stages.includes(stage.id)
        && (stage.decision?.input === name || Object.values(stage.bindings).some(binding => binding.input === name)));
}

function statusOf(job) {
    return { success: true, job_ref: job.job_ref, status: job.status, revision: job.revision,
        completed_stages: job.completed_stages, next_stage: job.definition.stages.find(stage => !job.completed_stages.includes(stage.id))?.id ?? null,
        ...(job.decision ? { decision: job.decision } : {}), ...(job.error ? { error: job.error } : {}) };
}

export function operationId() {
    const characters = Array.from(randomUUID());
    const timestamp = Date.now().toString(16).padStart(12, '0');
    characters.splice(0, 8, ...timestamp.slice(0, 8));
    characters.splice(9, 4, ...timestamp.slice(8));
    characters[14] = '7';
    return characters.join('');
}
