import fs from 'node:fs';
import path from 'node:path';
import { LearnedFunctions } from '../../modules/desktop-ui-mcp/learned-functions.mjs';
import { validateWorkflow } from '../../modules/desktop-ui-mcp/workflow-definition.mjs';
import { validateCompatibility } from '../../modules/desktop-ui-mcp/compatibility.mjs';

// The host validates reusable definitions without exposing verifier data to application tools.
const root = path.resolve(process.argv[2]);
const forbiddenLiterals = JSON.parse(process.argv[3] ?? '[]');
const maximumBytes = 1024 * 1024;
const documentFields = ['format', 'version', 'revision', 'application', 'functions', 'candidates'];
const functionFields = ['name', 'topic', 'steps', 'outputs', 'compatibility', 'validation', 'inputSchema'];
const applicationFields = ['key', 'version', 'process_name', 'identity_source'];

function requireKeys(value, allowed) {
    if (!value || typeof value !== 'object' || Object.keys(value).some(key => !allowed.includes(key))) {
        throw new Error('Reusable definitions contain unsupported state.');
    }
}

function inspectSelector(selector) {
    for (const value of Object.values(selector)) {
        if (Array.isArray(value)) {
            for (const part of value) inspectSelector(part);
        }
        if (typeof value === 'string' && forbiddenLiterals.some(literal => literal && value.includes(literal))) {
            throw new Error('Reusable selectors contain a literal training value.');
        }
    }
}

function inspectFunction(entry) {
    requireKeys(entry, functionFields);
    if (entry.compatibility.format) validateCompatibility(entry.compatibility);
    else requireKeys(entry.compatibility, ['node_version', 'platform', 'application_version']);
    for (const step of entry.steps) {
        requireKeys(step, ['action', 'selector', 'route', 'parameter']);
        inspectSelector(step.selector);
    }
    for (const output of entry.outputs) {
        requireKeys(output, ['name', 'selector', 'field', 'check']);
        requireKeys(output.check, ['kind', 'parameter']);
        inspectSelector(output.selector);
    }
    const parameters = entry.inputSchema.properties.parameters.properties;
    for (const schema of Object.values(parameters)) {
        requireKeys(schema, ['type', 'maxLength']);
    }
}

function inspectFile(file) {
    const parts = path.relative(root, file).split(path.sep);
    if (parts.length !== 4 || parts[0] !== 'programs' || !['generated-ui.json', 'workflows.json'].includes(parts[3])) {
        throw new Error('One active cartridge file belongs directly to its program/version directory.');
    }
    if (fs.statSync(file).size > maximumBytes) throw new Error('Reusable definition exceeds the byte limit.');
    const document = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (parts[3] === 'workflows.json') {
        inspectWorkflows(document, parts);
        return;
    }
    requireKeys(document, documentFields);
    requireKeys(document.application, applicationFields);
    if (document.application.key !== parts[1] || document.application.version !== parts[2]) {
        throw new Error('Reusable cartridge path and application identity disagree.');
    }
    new LearnedFunctions({ application: document.application, cartridgeRoot: root });
    for (const entry of document.functions) inspectFunction(entry);
    for (const candidate of document.candidates ?? []) {
        requireKeys(candidate, ['name', 'topic', 'action', 'selector']);
        inspectSelector(candidate.selector);
    }
}

function inspectWorkflows(document, parts) {
    requireKeys(document, ['format', 'application', 'workflows']);
    requireKeys(document.application, ['key', 'version']);
    if (document.format !== 'k-lani-bot-workflows/1' || !Array.isArray(document.workflows)
        || document.application.key !== parts[1] || document.application.version !== parts[2]) throw new Error('Workflow cartridge identity mismatch.');
    for (const definition of document.workflows) {
        rejectTrainingValues(definition);
        validateWorkflow(definition, (reference, application) => {
            const file = path.join(root, 'programs', application.key, application.version, 'generated-ui.json');
            const cartridge = JSON.parse(fs.readFileSync(file, 'utf8'));
            const functions = new LearnedFunctions({ cartridgeRoot: root, application: cartridge.application });
            return functions.get(reference.name);
        });
    }
}

function rejectTrainingValues(value) {
    if (typeof value === 'string' && forbiddenLiterals.some(literal => literal && value.includes(literal))) {
        throw new Error('Workflow definition contains literal training content.');
    }
    if (value && typeof value === 'object') for (const item of Object.values(value)) rejectTrainingValues(item);
}

function walk(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const file = path.join(directory, entry.name);
        if (entry.isSymbolicLink()) throw new Error('Reusable state rejects symbolic links.');
        if (entry.isDirectory()) walk(file);
        else if (entry.isFile()) inspectFile(file);
        else throw new Error('Reusable state requires regular files.');
    }
}

walk(root);
