import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { Workbench } from './workbench.mjs';
import { createServer, handleRpc } from './server.mjs';
import { McpClient } from '../mcp-client.mjs';

const template = '<!doctype html><html><p>{{input.values}}</p><p>{{sum.total}} EUR</p></html>';
const recipe = { name: 'report', parameters: ['values', 'folder', 'file'], steps: [
    { call: 'calculator.sum', arguments: { values: '$input.values' }, saveAs: 'sum' },
    { call: 'editor.write', arguments: { text: template }, saveAs: 'document' },
    { call: 'folder.create', arguments: { folder: '$input.folder' }, saveAs: 'folder' },
    { call: 'file.save', arguments: { folder: '$input.folder', file: '$input.file' }, saveAs: 'saved' },
    { call: 'file.read', arguments: { folder: '$input.folder', file: '$input.file' }, saveAs: 'verified' },
] };

async function fixture(context) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'k-lani-bot-browser-test-'));
    context.after(() => fs.rm(root, { recursive: true, force: true }));
    const workbench = new Workbench(root);
    return { root, workbench, call: (name, argumentsForFunction) => workbench.dispatch('cartridge_call', {
        function: name, arguments: argumentsForFunction }) };
}

test('a dynamically installed recipe creates real files with new inputs and discoverable schemas', async context => {
    const { root, workbench, call } = await fixture(context);
    assert.deepEqual((await workbench.dispatch('cartridge_browse', {})).topics,
        ['Administration', 'Use', 'Info', 'Programming']);
    assert.deepEqual((await workbench.dispatch('cartridge_browse', { topic: 'Use/Workflow' })).functions, []);
    await call('recipe.install', recipe);
    assert.deepEqual((await workbench.dispatch('cartridge_browse', { topic: 'Use/Workflow' })).functions, ['recipe.report']);
    const schema = await workbench.dispatch('cartridge_browse', { function: 'recipe.report' });
    assert.equal(schema.inputSchema.properties.values.type, 'array');
    const first = await call('recipe.report', { values: ['0.10', '0.20'], folder: 'first', file: 'report.html' });
    assert.equal(first.completedSteps, 5);
    assert.match(first.result.text, /0.30 EUR/);
    const second = await call('recipe.report', { values: ['11.20', '22.35', '3.05', '44.40', '5.00'],
        folder: 'second', file: 'report.html' });
    assert.match(second.result.text, /86.00 EUR/);
    assert.equal(await fs.readFile(path.join(root, 'Desktop/second/report.html'), 'utf8'), second.result.text);
    assert.equal(workbench.state().openedFile.sha256, second.result.sha256);
});

test('invalid inputs, path traversal and unsupported recipe operations are refused', async context => {
    const { workbench, call } = await fixture(context);
    await assert.rejects(call('folder.create', { folder: '../outside' }));
    await assert.rejects(call('calculator.sum', { values: ['1e10', '0.20'] }));
    await assert.rejects(call('recipe.install', { ...recipe, steps: [{ call: 'shell.execute',
        arguments: { command: 'anything' }, saveAs: 'output' }] }));
    await call('recipe.install', recipe);
    const eventsBefore = workbench.events.length;
    await assert.rejects(call('recipe.report', { values: ['1.00'], folder: '../outside', file: 'report.html' }));
    assert.equal(workbench.events.length, eventsBefore);
    await assert.rejects(call('recipe.install', recipe));
});

test('the documented output namespace resolves prior step results without changing legacy references', async context => {
    const { call } = await fixture(context);
    const namespaced = structuredClone(recipe);
    namespaced.steps[1].arguments.text = '<p>{{output.sum.total}}</p><p>{{sum.total}}</p>';
    await call('recipe.install', namespaced);
    const result = await call('recipe.report', { values: ['0.10', '0.20'], folder: 'namespace', file: 'report.html' });
    assert.equal(result.result.text, '<p>0.30</p><p>0.30</p>');
});

test('concurrent recipes cannot interleave editor contents and saving refuses overwrite', async context => {
    const { call } = await fixture(context);
    await call('recipe.install', recipe);
    const results = await Promise.all([
        call('recipe.report', { values: ['1.00'], folder: 'first', file: 'report.html' }),
        call('recipe.report', { values: ['2.00'], folder: 'second', file: 'report.html' }),
    ]);
    assert.match(results[0].result.text, /1.00 EUR/);
    assert.match(results[1].result.text, /2.00 EUR/);
    await assert.rejects(call('file.save', { folder: 'first', file: 'report.html' }));
    assert.match((await call('file.read', { folder: 'first', file: 'report.html' })).text, /1.00 EUR/);
});

test('the MCP wire uses the same application model and rejects foreign browser origins', async context => {
    const { workbench } = await fixture(context);
    const listing = await handleRpc({ jsonrpc: '2.0', id: 1, method: 'tools/list' }, workbench);
    assert.equal(listing.result.tools.length, 3);
    const server = createServer(workbench);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    context.after(() => new Promise(resolve => server.close(resolve)));
    const url = `http://127.0.0.1:${server.address().port}/mcp`;
    const body = JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: {
        name: 'cartridge_call', arguments: { function: 'calculator.sum', arguments: { values: ['12.40', '7.60'] } } } });
    const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body });
    assert.equal(JSON.parse((await response.json()).result.content[0].text).total, '20.00');
    const foreign = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json',
        Origin: 'https://other.example' }, body });
    assert.equal(foreign.status, 400);
    const client = new McpClient(process.execPath, [path.resolve('modules/efficiency-benchmark/browser-workbench/server.mjs'),
        '--stdio', `http://127.0.0.1:${server.address().port}`]);
    context.after(() => client.close());
    await client.connect();
    const tools = await client.request('tools/list', {});
    assert.equal(tools.result.tools.length, 3);
    const calculation = await client.call('cartridge_call', { function: 'calculator.sum', arguments: { values: ['0.10', '0.20'] } });
    assert.equal(JSON.parse(calculation.result.content[0].text).total, '0.30');
});
