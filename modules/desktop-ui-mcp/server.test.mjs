import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpClient } from '../efficiency-benchmark/mcp-client.mjs';
import { compactInstructions } from './gateway.mjs';

test('compact server exposes routing discovery without learning and rejects other learning calls', async context => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-result-server-'));
    const core = path.join(directory, 'core.mjs');
    fs.writeFileSync(core, `import readline from 'node:readline';
readline.createInterface({input:process.stdin}).on('line', line => {
    const request = JSON.parse(line);
    if (request.id === undefined) return;
    const result = request.method === 'initialize' ? {serverInfo:{name:'fixture',version:'test'}}
        : {content:[{type:'text',text:'{"success":true}'}]};
    process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:request.id,result})+'\\n');
});`);
    const configuration = path.join(directory, 'configuration.json');
    fs.writeFileSync(configuration, JSON.stringify({ command: process.execPath, arguments: [core] }));
    const client = new McpClient(process.execPath, [fileURLToPath(new URL('./server.mjs', import.meta.url)), configuration]);
    context.after(async () => {
        const exited = new Promise(resolve => client.process.once('exit', resolve));
        client.close();
        await exited;
        fs.rmSync(directory, { recursive: true, force: true });
    });
    await client.connect();
    const initialized = await client.request('initialize', { protocolVersion: '2025-06-18', capabilities: {},
        clientInfo: { name: 'fixture', version: 'test' } });
    assert.equal(initialized.result.instructions, compactInstructions);
    const response = await client.request('tools/list', {});
    assert.deepEqual(response.result.tools.map(tool => tool.name),
        ['ui_info', 'ui_observe', 'ui_act', 'ui_apply', 'ui_input', 'cartridge_info', 'cartridge_browse', 'cartridge_call']);
    const browse = await client.call('cartridge_browse', { function: 'Info.result' });
    assert.equal(JSON.parse(browse.result.content[0].text).function.name, 'Info.result');
    const learning = await client.call('cartridge_browse', { function: 'Programming.learn' });
    assert.equal(learning.result.isError, true);
    assert.match(learning.result.content[0].text, /configured for learning/);
});
