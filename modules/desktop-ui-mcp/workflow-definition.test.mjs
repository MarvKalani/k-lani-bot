import test from 'node:test';
import assert from 'node:assert/strict';
import { validateWorkflow, validateParameters, resolveBindings, definitionHash } from './workflow-definition.mjs';

const functionEntry = { validation: 'demonstrated_once; every execution verifies outputs', compatibility: { node_version: 'test' },
    inputSchema: { properties: { parameters: { properties: { text: { type: 'string' } }, required: ['text'] } } }, outputs: [{ name: 'value' }] };

function definition() {
    const stage = id => ({ id, function_ref: { application: 'editor', name: 'write' }, bindings: { text: { input: 'text' } },
        preconditions: 'fresh_unique_enabled_targets', postconditions: 'recorded_function_outputs' });
    return { format: 'k-lani-bot-workflow/1', key: 'example', revision: 1, inputs: { text: { type: 'string', maxLength: 16000 } },
        applications: { editor: { key: 'editor', version: '1.0', window: 'main' } }, stages: [stage('open'), stage('write')],
        outputs: { result: { stage: 'write', output: 'value' } } };
}

test('typed staged definitions and previous confirmed output bindings contain no literal runtime inputs', () => {
    const workflow = definition();
    workflow.stages[1].bindings.text = { stage: 'open', output: 'value' };
    validateWorkflow(workflow, () => functionEntry);
    const parameters = { text: 'PRIVATE_RUNTIME_ADDRESS' };
    validateParameters(workflow.inputs, parameters);
    assert.equal(resolveBindings(workflow.stages[1].bindings, parameters, { open: { value: 'confirmed text' } }).text, 'confirmed text');
    assert.equal(JSON.stringify(workflow).includes(parameters.text), false);
    assert.match(definitionHash(workflow), /^[0-9a-f]{64}$/);
});

test('unknown functions, forward references, cycles, undeclared inputs, runtime IDs and expressions are rejected', () => {
    for (const change of [workflow => { workflow.stages[0].bindings.text = { stage: 'write', output: 'value' }; },
        workflow => { workflow.stages[1].bindings.text = { stage: 'write', output: 'value' }; },
        workflow => { workflow.stages[0].bindings.text = { input: 'undeclared' }; },
        workflow => { workflow.stages[0].pid = 42; }, workflow => { workflow.stages[0].bindings.text = { expression: 'process.env.SECRET' }; },
        workflow => { delete workflow.key; }, workflow => { delete workflow.stages[0].id; },
        workflow => { workflow.stages[0].bindings.text = { value: 'literal training address' }; }]) {
        const workflow = definition();
        change(workflow);
        assert.throws(() => validateWorkflow(workflow, () => functionEntry));
    }
    assert.throws(() => validateWorkflow(definition(), () => null), /Unknown or unverified/);
});

test('finite enums, numbers, booleans and opaque documents are checked; host paths are not document refs', () => {
    const inputs = { model: { type: 'enum', values: ['Basic', 'Wide'] }, count: { type: 'number', minimum: 1 },
        confirm: { type: 'boolean' }, document: { type: 'document' } };
    const parameters = { model: 'Basic', count: 1, confirm: true, document: { ref: 'opaque-reference-123', sha256: 'a'.repeat(64), origin: 'fixture' } };
    validateParameters(inputs, parameters);
    for (const change of [{ model: 'Other' }, { count: NaN }, { confirm: 'true' },
        { document: { path: '/home/private.txt', sha256: 'a'.repeat(64), origin: 'fixture' } }]) {
        assert.throws(() => validateParameters(inputs, { ...parameters, ...change }));
    }
});

test('binding types, explicit boolean decisions and verified function effects are mandatory', () => {
    const workflow = definition();
    workflow.inputs.text = { type: 'number' };
    assert.throws(() => validateWorkflow(workflow, () => functionEntry), /Binding type/);
    const decision = definition();
    decision.stages[0].decision = { input: 'text', prompt: 'Confirm' };
    assert.throws(() => validateWorkflow(decision, () => functionEntry), /explicit workflow decision/);
    assert.throws(() => validateWorkflow(definition(), () => ({ ...functionEntry, validation: 'candidate' })), /unverified/);
    const enumWorkflow = definition();
    enumWorkflow.inputs.text = { type: 'enum', values: ['Basic', 'Express'] };
    validateWorkflow(enumWorkflow, () => functionEntry);
});
