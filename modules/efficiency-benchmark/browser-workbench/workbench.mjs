import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';

const objectSchema = properties => ({ type: 'object', properties,
    required: Object.keys(properties), additionalProperties: false });
const textSchema = { type: 'string', maxLength: 24000 };
const nameSchema = { type: 'string', pattern: '^[A-Za-z0-9_-]{1,64}$' };
const fileSchema = { type: 'string', pattern: '^[A-Za-z0-9_-]{1,64}\\.html$' };
const valuesSchema = { type: 'array', minItems: 1, maxItems: 20,
    items: { type: 'string', pattern: '^\\d{1,7}\\.\\d{2}$' } };
const definitions = {
    'calculator.sum': { topic: 'Use/Calculator', description: 'Add decimal amounts in the calculator; return exact cents and total.',
        inputSchema: objectSchema({ values: valuesSchema }) },
    'editor.write': { topic: 'Use/Editor', description: 'Replace the editor document with the supplied text.',
        inputSchema: objectSchema({ text: textSchema }) },
    'folder.create': { topic: 'Use/Files', description: 'Create a folder inside the isolated test Desktop.',
        inputSchema: objectSchema({ folder: nameSchema }) },
    'file.save': { topic: 'Use/Files', description: 'Save the current editor document; refuse overwriting an existing file.',
        inputSchema: objectSchema({ folder: nameSchema, file: fileSchema }) },
    'file.read': { topic: 'Use/Files', description: 'Reopen a saved HTML file; return its exact text, bytes and SHA-256.',
        inputSchema: objectSchema({ folder: nameSchema, file: fileSchema }) },
    'recipe.install': { topic: 'Programming', description: 'Install a named recipe of existing functions. No JavaScript or shell execution.',
        inputSchema: objectSchema({ name: nameSchema, parameters: { type: 'array', minItems: 1, maxItems: 8, items: nameSchema },
            steps: { type: 'array', minItems: 1, maxItems: 16, items: objectSchema({ call: { type: 'string' },
                arguments: { type: 'object', description: 'Use $input.parameter for inputs or $output.stepName.property for prior results. '
                    + 'Strings interpolate {{input.parameter}} or {{output.stepName.property}}. stepName is a preceding saveAs value.' },
                saveAs: { ...nameSchema, description: 'Bind this step result under this name for later steps.' } }) } }) },
};

export const gatewayTools = [
    { name: 'cartridge_info', description: 'Identify the isolated browser workbench and available access routes.',
        inputSchema: objectSchema({}) },
    { name: 'cartridge_browse', description: 'Browse a compact topic index or load one function schema.',
        inputSchema: { type: 'object', properties: { topic: { type: 'string' }, function: { type: 'string' } },
            additionalProperties: false } },
    { name: 'cartridge_call', description: 'Execute a discovered function or installed recipe in the isolated workbench.',
        inputSchema: objectSchema({ function: { type: 'string' }, arguments: { type: 'object' } }) },
];

function validate(schema, value) {
    if (schema.type === 'string') {
        if (typeof value !== 'string' || value.length > (schema.maxLength ?? 24000)
            || schema.pattern && !new RegExp(schema.pattern).test(value)) throw new Error('Invalid string argument.');
    } else if (schema.type === 'array') {
        if (!Array.isArray(value) || value.length < schema.minItems || value.length > schema.maxItems) {
            throw new Error('Invalid array argument.');
        }
        for (const item of value) validate(schema.items, item);
    } else if (schema.type === 'object') {
        if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Expected an object.');
        for (const name of schema.required ?? []) {
            if (!Object.hasOwn(value, name)) throw new Error(`Missing argument: ${name}`);
        }
        for (const [name, entry] of Object.entries(value)) {
            if (!schema.properties?.[name]) {
                if (schema.additionalProperties === false) throw new Error(`Unknown argument: ${name}`);
            } else validate(schema.properties[name], entry);
        }
    }
}

function resolveReference(reference, bindings) {
    const names = reference.split('.');
    let value = bindings;
    for (const name of names) {
        if (!value || !Object.hasOwn(value, name)) throw new Error(`Unknown recipe reference: ${reference}`);
        value = value[name];
    }
    return value;
}

function resolveArguments(value, bindings) {
    if (typeof value === 'string') {
        if (/^\$[A-Za-z0-9_.]+$/.test(value)) return resolveReference(value.slice(1), bindings);
        return value.replace(/\{\{([A-Za-z0-9_.]+)\}\}/g, (_, reference) => {
            const resolved = resolveReference(reference, bindings);
            return Array.isArray(resolved) ? resolved.join(', ') : String(resolved);
        });
    }
    if (Array.isArray(value)) return value.map(entry => resolveArguments(entry, bindings));
    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([name, entry]) => [name, resolveArguments(entry, bindings)]));
    }
    return value;
}

// Both human controls and program tools call this same application model in a disposable filesystem root.
export class Workbench {
    constructor(root) {
        this.root = path.resolve(root);
        this.document = '';
        this.calculation = null;
        this.openedFile = null;
        this.files = [];
        this.recipes = new Map();
        this.events = [];
        this.sequence = Promise.resolve();
    }

    dispatch(name, argumentsForTool) {
        const execution = this.sequence.then(() => this.dispatchSequential(name, argumentsForTool));
        this.sequence = execution.catch(() => {});
        return execution;
    }

    async dispatchSequential(name, argumentsForTool) {
        const tool = gatewayTools.find(entry => entry.name === name);
        if (!tool) throw new Error('Unknown gateway tool.');
        validate(tool.inputSchema, argumentsForTool);
        if (name === 'cartridge_info') return { program: 'synthetic-browser-workbench', version: '1.1.0',
            platform: 'browser + isolated filesystem', routes: ['application API', 'Computer Use', 'WebMCP', 'stdio MCP'],
            compatibility: 'Only this fixture version; no Windows editor or calculator coverage.' };
        if (name === 'cartridge_browse') return this.browse(argumentsForTool);
        const { function: functionName, arguments: functionArguments } = argumentsForTool;
        if (functionName.startsWith('recipe.')) {
            if (functionName === 'recipe.install') return this.install(functionArguments);
            return this.runRecipe(functionName.slice(7), functionArguments);
        }
        return this.execute(functionName, functionArguments);
    }

    browse({ topic, function: functionName }) {
        if (functionName) {
            const definition = definitions[functionName];
            if (definition) return { name: functionName, ...definition };
            const recipe = this.recipes.get(functionName.replace(/^recipe\./, ''));
            if (recipe) return { name: `recipe.${recipe.name}`, topic: 'Use/Workflow',
                description: 'Execute the installed recipe; return the final step result and an evidence reference.',
                inputSchema: recipe.inputSchema, steps: recipe.steps.length };
            throw new Error('Unknown function.');
        }
        if (!topic) return { topics: ['Administration', 'Use', 'Info', 'Programming'] };
        if (topic === 'Use') return { topics: ['Use/Calculator', 'Use/Editor', 'Use/Files', 'Use/Workflow'] };
        if (topic === 'Info') return { functions: ['cartridge_info'], coverage: 'Synthetic fixture only.' };
        if (topic === 'Administration') return { implemented: [], missing: ['preferences', 'account management'] };
        if (topic === 'Use/Workflow') return { functions: [...this.recipes.keys()].map(name => `recipe.${name}`) };
        return { functions: Object.entries(definitions).filter(([, entry]) => entry.topic === topic)
            .map(([name, entry]) => ({ name, description: entry.description })) };
    }

    install(argumentsForFunction) {
        validate(definitions['recipe.install'].inputSchema, argumentsForFunction);
        if (this.recipes.has(argumentsForFunction.name)) throw new Error('Recipe already exists.');
        if (new Set(argumentsForFunction.parameters).size !== argumentsForFunction.parameters.length) {
            throw new Error('Duplicate parameter.');
        }
        const outputs = new Set(['input', 'output']);
        for (const step of argumentsForFunction.steps) {
            if (!definitions[step.call] || step.call === 'recipe.install') throw new Error('Unsupported recipe function.');
            if (outputs.has(step.saveAs) || ['__proto__', 'constructor', 'prototype'].includes(step.saveAs)) {
                throw new Error('Duplicate or reserved output name.');
            }
            outputs.add(step.saveAs);
        }
        const recipe = structuredClone(argumentsForFunction);
        recipe.inputSchema = this.recipeSchema(recipe);
        this.recipes.set(recipe.name, recipe);
        return { installed: `recipe.${argumentsForFunction.name}`, steps: argumentsForFunction.steps.length,
            indexUpdated: true, restartRequired: false };
    }

    recipeSchema(recipe) {
        const properties = Object.fromEntries(recipe.parameters.map(parameter => [parameter, textSchema]));
        for (const step of recipe.steps) {
            for (const [name, value] of Object.entries(step.arguments)) {
                if (typeof value !== 'string' || !value.startsWith('$input.')) continue;
                const parameter = value.slice(7);
                if (!Object.hasOwn(properties, parameter)) throw new Error('Unknown recipe parameter.');
                const schema = definitions[step.call].inputSchema.properties[name];
                if (!schema) throw new Error('Unknown function argument.');
                properties[parameter] = schema;
            }
        }
        return objectSchema(properties);
    }

    async runRecipe(name, inputs) {
        const recipe = this.recipes.get(name);
        if (!recipe) throw new Error('Unknown recipe.');
        validate(recipe.inputSchema, inputs);
        const bindings = { input: inputs, output: {} };
        let result;
        const start = this.events.length;
        for (const step of recipe.steps) {
            const argumentsForStep = resolveArguments(step.arguments, bindings);
            result = await this.execute(step.call, argumentsForStep);
            bindings[step.saveAs] = result;
            bindings.output[step.saveAs] = result;
        }
        return { result, completedSteps: recipe.steps.length, evidence: { from: start + 1, to: this.events.length } };
    }

    async execute(name, argumentsForFunction) {
        const definition = definitions[name];
        if (!definition || name === 'recipe.install') throw new Error('Unknown application function.');
        validate(definition.inputSchema, argumentsForFunction);
        let result;
        if (name === 'calculator.sum') result = this.sum(argumentsForFunction.values);
        if (name === 'editor.write') {
            this.document = argumentsForFunction.text;
            result = { bytes: Buffer.byteLength(this.document, 'utf8') };
        }
        if (name === 'folder.create') {
            await fs.mkdir(this.folderPath(argumentsForFunction.folder), { recursive: true });
            result = { folder: `Desktop/${argumentsForFunction.folder}` };
        }
        if (name === 'file.save') result = await this.save(argumentsForFunction);
        if (name === 'file.read') result = await this.read(argumentsForFunction);
        this.events.push({ step: this.events.length + 1, function: name, result: structuredClone(result) });
        return result;
    }

    sum(values) {
        const cents = values.reduce((total, value) => total + Number(value.replace('.', '')), 0);
        this.calculation = { values: [...values], cents, total: `${Math.floor(cents / 100)}.${String(cents % 100).padStart(2, '0')}` };
        return this.calculation;
    }

    folderPath(folder) {
        return path.join(this.root, 'Desktop', folder);
    }

    async save({ folder, file }) {
        const location = path.join(this.folderPath(folder), file);
        await fs.writeFile(location, this.document, { encoding: 'utf8', flag: 'wx' });
        const relative = `Desktop/${folder}/${file}`;
        this.files.push(relative);
        return { path: relative, bytes: Buffer.byteLength(this.document, 'utf8'),
            sha256: createHash('sha256').update(this.document).digest('hex') };
    }

    async read({ folder, file }) {
        const text = await fs.readFile(path.join(this.folderPath(folder), file), 'utf8');
        this.document = text;
        this.openedFile = { path: `Desktop/${folder}/${file}`, text, bytes: Buffer.byteLength(text, 'utf8'),
            sha256: createHash('sha256').update(text).digest('hex') };
        return this.openedFile;
    }

    state() {
        return { document: this.document, calculation: this.calculation, files: this.files, events: this.events,
            openedFile: this.openedFile,
            recipes: [...this.recipes.keys()] };
    }
}
