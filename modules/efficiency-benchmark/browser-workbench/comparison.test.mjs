import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { Comparison } from './comparison.mjs';
import { createServer } from './server.mjs';
import { McpClient } from '../mcp-client.mjs';

const recipe = JSON.parse(await fs.readFile(new URL('./recipe.example.json', import.meta.url), 'utf8'));

async function fixture(context, dataset = 0) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'k-lani-bot-comparison-'));
    context.after(() => fs.rm(root, { recursive: true, force: true }));
    return new Comparison(root, dataset);
}

async function complete(comparison, name) {
    const session = comparison.session(name);
    await session.dispatch('cartridge_call', { function: 'recipe.install', arguments: recipe });
    await session.dispatch('cartridge_call', { function: `recipe.${recipe.name}`, arguments: {
        values: comparison.task().values, folder: comparison.folder, file: comparison.file } });
}

test('both sessions start identically and one completed route cannot provide the other route result', async context => {
    const comparison = await fixture(context);
    assert.deepEqual(comparison.session('computer-use').state(), comparison.session('mcp').state());
    assert.notEqual(comparison.session('computer-use').root, comparison.session('mcp').root);
    assert.equal((await comparison.verify()).status, 'pending');
    await complete(comparison, 'mcp');
    assert.equal(comparison.session('computer-use').document, '');
    assert.equal(comparison.session('computer-use').events.length, 0);
    const half = await comparison.verify();
    assert.equal(half.equivalent, false);
    assert.equal(half.sessions.mcp.status, 'passed');
    assert.equal(half.sessions['computer-use'].status, 'pending');
    await complete(comparison, 'computer-use');
    const both = await comparison.verify();
    assert.equal(both.equivalent, true);
    assert.equal(both.sessions.mcp.sha256, both.sessions['computer-use'].sha256);
    assert.equal(both.modelUsage.inputTokens, null);
});

test('independent verification detects tampered output, missing reopening and additional files', async context => {
    const comparison = await fixture(context, 1);
    await complete(comparison, 'mcp');
    const workbench = comparison.session('mcp');
    workbench.openedFile = null;
    assert.equal((await comparison.verifySession('mcp')).checks.reopened, false);
    await workbench.dispatch('cartridge_call', { function: 'file.read', arguments: { folder: comparison.folder, file: comparison.file } });
    const directory = path.join(workbench.root, 'Desktop', comparison.folder);
    await fs.writeFile(path.join(directory, 'unexpected.html'), 'Extra output');
    assert.equal((await comparison.verifySession('mcp')).checks.noOtherOutput, false);
    await fs.writeFile(path.join(directory, comparison.file), 'Incorrect result');
    assert.equal((await comparison.verifySession('mcp')).checks.fileContents, false);
});

test('a correct saved document stays pending until it is reopened', async context => {
    const comparison = await fixture(context);
    const session = comparison.session('computer-use');
    const call = (name, argumentsForFunction) => session.dispatch('cartridge_call', {
        function: name, arguments: argumentsForFunction });
    await call('calculator.sum', { values: comparison.task().values });
    await call('folder.create', { folder: comparison.folder });
    await call('editor.write', { text: comparison.expectedText });
    await call('file.save', { folder: comparison.folder, file: comparison.file });
    const result = await comparison.verifySession('computer-use');
    assert.equal(result.status, 'pending');
    assert.equal(result.successful, false);
    await call('file.read', { folder: comparison.folder, file: comparison.file });
    assert.equal((await comparison.verifySession('computer-use')).status, 'passed');
});

test('the output contract is independent of the participant recipe template', async context => {
    const comparison = await fixture(context);
    const session = comparison.session('mcp');
    const incorrectRecipe = structuredClone(recipe);
    incorrectRecipe.steps[1].arguments.text = '<html><p>Wrong sum: 999.00 EUR</p></html>';
    await session.dispatch('cartridge_call', { function: 'recipe.install', arguments: incorrectRecipe });
    await session.dispatch('cartridge_call', { function: `recipe.${recipe.name}`, arguments: {
        values: comparison.task().values, folder: comparison.folder, file: comparison.file } });
    const result = await comparison.verifySession('mcp');
    assert.equal(result.checks.calculation, true);
    assert.equal(result.checks.fileContents, false);
    assert.equal(result.status, 'failed');
});

test('the stdio bridge preserves its selected session and cannot silently operate the sibling', async context => {
    const comparison = await fixture(context);
    const server = createServer(comparison.session('mcp'), comparison);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    context.after(() => new Promise(resolve => server.close(resolve)));
    const origin = `http://127.0.0.1:${server.address().port}`;
    const client = new McpClient(process.execPath, [path.resolve('modules/efficiency-benchmark/browser-workbench/server.mjs'),
        '--stdio', `${origin}/?session=computer-use`]);
    context.after(() => client.close());
    await client.connect();
    await client.call('cartridge_call', { function: 'calculator.sum', arguments: { values: ['0.10', '0.20'] } });
    assert.equal(comparison.session('computer-use').calculation.total, '0.30');
    assert.equal(comparison.session('mcp').calculation, null);
    const invalid = await fetch(`${origin}/state?session=unknown`);
    assert.equal(invalid.status, 400);
    const initial = await (await fetch(`${origin}/state?session=mcp`)).json();
    assert.deepEqual(initial.task.values, comparison.task().values);
});
