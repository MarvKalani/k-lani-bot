const text = { type: 'string' };
const revision = { type: 'string', description: 'Opaque revision from the latest matching form response.' };
const ref = { type: 'string', description: 'Observed session/node/application/form reference, not a path or native ID.' };
const form = { form_ref: ref, context_rev: revision };
const field = { ...form, field_ref: ref };
const page = { cursor: ref, limit: { type: 'integer', minimum: 1, maximum: 100 } };

const definition = (name, description, properties, required) => ({ name, topic: 'Use/Forms', description,
    inputSchema: { type: 'object', properties, required, additionalProperties: false } });

export const formDefinitions = [
    definition('form.read', 'Read a currently observed form scope and native field metadata. Unknown constraints remain unknown. '
        + 'Values and refs are volatile; no temporal observation is treated as a causal rule.',
    { pid: { type: 'integer', minimum: 1 }, scope: ref, fields: { type: 'array', items: text, uniqueItems: true, maxItems: 20 }, ...page }, ['pid', 'scope']),
    definition('form.options', 'Read real options for a current field/context. An explicit native dropdown opening may be recorded; '
        + 'it must not select a value. Follow pages until complete.',
    { ...field, ...page }, ['form_ref', 'field_ref', 'context_rev']),
    definition('form.set', 'Set bounded literal text or one observed option in the exact current context. All revisions and targets '
        + 'are freshly checked before input, and the result afterward. Never replay an unconfirmed mutation.',
    { ...field, value: { type: 'string', maxLength: 16000 }, option_id: ref, options_rev: revision }, ['form_ref', 'field_ref', 'context_rev']),
    definition('form.validate', 'Read-only validation of current required values and options. Unknown constraints prevent claiming ready.',
    form, ['form_ref', 'context_rev'])
];

export function validateFormArguments(name, args) {
    const definition = formDefinitions.find(entry => entry.name === name);
    if (!definition || !args || typeof args !== 'object' || Array.isArray(args)
        || Object.keys(args).some(key => !Object.hasOwn(definition.inputSchema.properties, key))
        || definition.inputSchema.required.some(key => typeof args[key] !== (key === 'pid' ? 'number' : 'string'))) {
        throw new Error('Invalid form arguments.');
    }
    for (const [key, schema] of Object.entries(definition.inputSchema.properties)) {
        if (args[key] === undefined) continue;
        if (schema.type === 'string' && (typeof args[key] !== 'string' || args[key].length > (schema.maxLength ?? 256))
            || schema.type === 'integer' && (!Number.isInteger(args[key]) || args[key] < schema.minimum || schema.maximum && args[key] > schema.maximum)
            || schema.type === 'array' && (!Array.isArray(args[key]) || args[key].length > schema.maxItems
                || new Set(args[key]).size !== args[key].length || args[key].some(value => typeof value !== 'string'))) {
            throw new Error('Invalid form argument: ' + key);
        }
    }
    if (name === 'form.set' && (Object.hasOwn(args, 'value') === Object.hasOwn(args, 'option_id')
        || args.option_id !== undefined && typeof args.options_rev !== 'string'
        || args.value !== undefined && args.options_rev !== undefined)) throw new Error('Select exactly one typed form value.');
}
