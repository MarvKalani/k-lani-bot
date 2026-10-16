import { createHash } from 'node:crypto';

const identifier = /^[a-z][a-z0-9._-]{0,79}$/;
const valueTypes = ['string', 'number', 'boolean', 'enum', 'document'];
const maximumStages = 32;

export function definitionHash(definition) {
    return createHash('sha256').update(JSON.stringify(definition)).digest('hex');
}

// Workflow definitions contain only typed contracts and references to verified functions, never runtime PIDs or training literals.
export function validateWorkflow(definition, resolveFunction) {
    requireKeys(definition, ['format', 'key', 'revision', 'inputs', 'applications', 'stages', 'outputs']);
    if (definition.format !== 'k-lani-bot-workflow/1' || !validIdentifier(definition.key)
        || !Number.isInteger(definition.revision) || definition.revision < 1) throw new Error('Invalid workflow identity/revision.');
    if (!boundedObject(definition.inputs) || !boundedObject(definition.applications) || !boundedObject(definition.outputs)
        || Object.keys(definition.applications).length < 1
        || !Array.isArray(definition.stages) || definition.stages.length < 1 || definition.stages.length > maximumStages) {
        throw new Error('A workflow needs bounded typed inputs, applications, stages and outputs.');
    }
    for (const [name, schema] of Object.entries(definition.inputs)) {
        if (!identifier.test(name)) throw new Error('Invalid workflow input name.');
        validateType(schema);
    }
    for (const [name, application] of Object.entries(definition.applications)) {
        requireKeys(application, ['key', 'version', 'process_name', 'window']);
        if (!identifier.test(name) || !validIdentifier(application.key) || typeof application.version !== 'string'
            || application.version === 'unknown' || !/^[a-zA-Z0-9._-]{1,80}$/.test(application.version)
            || typeof application.process_name !== 'string' && application.window === undefined
            || application.window !== undefined && !['main', 'dialog'].includes(application.window)) throw new Error('Invalid application contract.');
    }
    const prior = new Map();
    for (const stage of definition.stages) validateStage(stage, definition, prior, resolveFunction);
    for (const [name, binding] of Object.entries(definition.outputs)) {
        if (!identifier.test(name)) throw new Error('Invalid workflow output name.');
        validateBinding(binding, definition.inputs, prior);
    }
    return definition;
}

function validateStage(stage, definition, prior, resolveFunction) {
    requireKeys(stage, ['id', 'function_ref', 'bindings', 'preconditions', 'postconditions', 'decision']);
    requireKeys(stage.function_ref, ['application', 'name']);
    if (!validIdentifier(stage.id) || prior.has(stage.id) || !validIdentifier(stage.function_ref.application)
        || !Object.hasOwn(definition.applications, stage.function_ref.application)
        || typeof stage.function_ref.name !== 'string') throw new Error('Invalid or duplicate workflow stage.');
    const entry = resolveFunction(stage.function_ref, definition.applications[stage.function_ref.application]);
    if (!entry || !Array.isArray(entry.outputs) || !entry.outputs.length || !entry.compatibility
        || typeof entry.validation !== 'string' || !entry.validation.startsWith('demonstrated_once')) {
        throw new Error('Unknown or unverified workflow function.');
    }
    if (stage.preconditions !== 'fresh_unique_enabled_targets' || stage.postconditions !== 'recorded_function_outputs') {
        throw new Error('Stages require fresh target checks and the verified function postconditions.');
    }
    const schema = entry.inputSchema.properties.parameters;
    if (!stage.bindings || Object.keys(stage.bindings).some(name => !Object.hasOwn(schema.properties, name))
        || schema.required.some(name => !Object.hasOwn(stage.bindings, name))) throw new Error('Stage bindings do not match the function contract.');
    for (const [name, binding] of Object.entries(stage.bindings)) {
        validateBinding(binding, definition.inputs, prior);
        const sourceType = binding.input ? definition.inputs[binding.input].type : prior.get(binding.stage).get(binding.output);
        const targetType = schema.properties[name].type;
        if (sourceType !== targetType && !(sourceType === 'enum' && targetType === 'string')) {
            throw new Error('Binding type does not match its function parameter.');
        }
    }
    if (stage.decision !== undefined) {
        requireKeys(stage.decision, ['input', 'prompt']);
        if (!validIdentifier(stage.decision.input) || definition.inputs[stage.decision.input]?.type !== 'boolean'
            || typeof stage.decision.prompt !== 'string' || stage.decision.prompt.length < 1
            || stage.decision.prompt.length > 512) throw new Error('Invalid explicit workflow decision.');
    }
    prior.set(stage.id, new Map(entry.outputs.map(output => [output.name, output.type ?? 'string'])));
}

function validateBinding(binding, inputs, prior) {
    requireKeys(binding, ['input', 'stage', 'output']);
    if (Object.hasOwn(binding, 'input')) {
        if (Object.keys(binding).length !== 1 || !validIdentifier(binding.input)
            || !Object.hasOwn(inputs, binding.input)) throw new Error('Binding names an undeclared input.');
        return;
    }
    if (Object.keys(binding).length !== 2 || !validIdentifier(binding.stage) || !validIdentifier(binding.output)
        || !prior.get(binding.stage)?.has(binding.output)) {
        throw new Error('Binding refers forward, forms a cycle, or names an unconfirmed/unknown output.');
    }
}

function validateType(schema) {
    requireKeys(schema, ['type', 'required', 'maxLength', 'minimum', 'maximum', 'values']);
    if (!valueTypes.includes(schema.type) || schema.required !== undefined && typeof schema.required !== 'boolean') throw new Error('Invalid workflow value type.');
    if (schema.type === 'enum' && (!Array.isArray(schema.values) || schema.values.length < 1 || schema.values.length > 100
        || schema.values.some(value => typeof value !== 'string') || new Set(schema.values).size !== schema.values.length)) throw new Error('Invalid enum choices.');
    if (schema.maxLength !== undefined && (!Number.isInteger(schema.maxLength) || schema.maxLength < 1 || schema.maxLength > 16000)) throw new Error('Invalid bounded input length.');
    for (const bound of ['minimum', 'maximum']) if (schema[bound] !== undefined && !Number.isFinite(schema[bound])) throw new Error('Invalid numeric bound.');
    if (schema.minimum !== undefined && schema.maximum !== undefined && schema.minimum > schema.maximum) throw new Error('Invalid numeric range.');
}

export function validateParameters(inputs, parameters, allowMissing = false) {
    requireKeys(parameters, Object.keys(inputs));
    for (const [name, schema] of Object.entries(inputs)) {
        if (!Object.hasOwn(parameters, name)) {
            if (!allowMissing && schema.required !== false) throw new Error('Missing workflow parameter: ' + name);
            continue;
        }
        validateValue(parameters[name], schema);
    }
}

function validateValue(value, schema) {
    if (schema.type === 'string' && (typeof value !== 'string' || value.length > (schema.maxLength ?? 16000))
        || schema.type === 'number' && (!Number.isFinite(value) || schema.minimum !== undefined && value < schema.minimum
            || schema.maximum !== undefined && value > schema.maximum)
        || schema.type === 'boolean' && typeof value !== 'boolean'
        || schema.type === 'enum' && !schema.values.includes(value)) throw new Error('Workflow parameter does not match its declared type.');
    if (schema.type === 'document') {
        requireKeys(value, ['ref', 'sha256', 'origin']);
        if (typeof value.ref !== 'string' || !/^[a-z0-9-]{16,80}$/i.test(value.ref) || !/^[a-f0-9]{64}$/.test(value.sha256)
            || typeof value.origin !== 'string' || !identifier.test(value.origin)) throw new Error('Use an opaque document reference, digest and provenance.');
    }
}

export function resolveBindings(bindings, parameters, outputs) {
    return Object.fromEntries(Object.entries(bindings).map(([name, binding]) => [name,
        Object.hasOwn(binding, 'input') ? parameters[binding.input] : outputs[binding.stage][binding.output]]));
}

function requireKeys(value, allowed) {
    if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(key => !allowed.includes(key))) {
        throw new Error('Workflow contracts reject unsupported fields, paths, runtime IDs and executable expressions.');
    }
}

function boundedObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).length <= 100;
}

function validIdentifier(value) {
    return typeof value === 'string' && identifier.test(value);
}
