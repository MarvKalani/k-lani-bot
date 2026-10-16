import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import readline from 'node:readline';

test('vision route excludes semantic information and accounts for refused calls', async () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'benchmark-meter-test-'));
    const gatewayFile = path.join(directory, 'gateway.mjs');
    const fixture = `import readline from 'node:readline';
readline.createInterface({input:process.stdin}).on('line',line=>{
const frame=JSON.parse(line);let result={};
if(frame.method==='tools/list')result={tools:[{name:'mouse_click',description:'physical input',
inputSchema:{type:'object',properties:{x:{type:'integer'},y:{type:'integer'},id:{type:'integer'}},required:[]}},
{name:'get_ui_tree',inputSchema:{properties:{}}}]};
else result={content:[{type:'text',text:'private semantic object label'}],structuredContent:{success:true,clickInfo:{name:'private semantic object label'}}};
process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:frame.id,result})+'\\n');
});`;
    fs.writeFileSync(gatewayFile, fixture);
    const metricsFile = path.join(directory, 'metrics.json');
    const configurationFile = path.join(directory, 'config.json');
    fs.writeFileSync(configurationFile, JSON.stringify({ command: process.execPath, arguments: [gatewayFile],
        route: 'screenshot-adapter', metricsFile, tools: ['mouse_click', 'save_macro', 'run_macro'] }));
    const meter = spawn(process.execPath, ['modules/efficiency-benchmark/mcp-meter.mjs', configurationFile], { stdio: ['pipe', 'pipe', 'pipe'] });
    const responses = new Map();
    const waiting = new Map();
    readline.createInterface({ input: meter.stdout }).on('line', line => {
        const response = JSON.parse(line);
        responses.set(response.id, response);
        waiting.get(response.id)?.(response);
    });
    function request(id, method, params = {}) {
        return new Promise(resolve => {
            waiting.set(id, resolve);
            meter.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
        });
    }
    try {
        const catalog = await request(1, 'tools/list');
        assert.equal(catalog.result.tools.length, 1);
        assert.equal(catalog.result.tools[0].inputSchema.properties.id, undefined);
        const clicked = await request(2, 'tools/call', { name: 'mouse_click', arguments: { x: 1, y: 2 } });
        assert.equal(JSON.stringify(clicked).includes('private semantic object label'), false);
        const refused = await request(3, 'tools/call', { name: 'mouse_click', arguments: { id: 1 } });
        assert.equal(refused.error.code, -32602);
        const metrics = JSON.parse(fs.readFileSync(metricsFile));
        assert.equal(metrics.calls.length, 2);
        assert.equal(metrics.calls[1].failed, true);
        const semanticMacro = await request(4, 'tools/call', { name: 'save_macro', arguments: {
            name: 'not_allowed', steps: [{ action: 'get_ui_tree', args: {} }] } });
        assert.equal(semanticMacro.error.code, -32602);
        const coordinateMacro = await request(5, 'tools/call', { name: 'save_macro', arguments: {
            name: 'not_allowed', steps: [{ action: 'mouse_click', args: { x: 1, y: 2 } }] } });
        assert.equal(coordinateMacro.error.code, -32602);
        const keyboardMacro = await request(6, 'tools/call', { name: 'save_macro', arguments: {
            name: 'keys_only', steps: [{ action: 'hotkey', args: { keys: ['ctrl', 'a'] } },
                { action: 'set_clipboard', args: { text: 'sample' } }, { action: 'hotkey', args: { keys: ['ctrl', 'v'] } }] } });
        assert.equal(keyboardMacro.result.isError, undefined);
        assert.equal(JSON.stringify(keyboardMacro).includes('private semantic object label'), false);
    } finally {
        meter.stdin.end();
        await new Promise(resolve => meter.once('close', resolve));
        fs.rmSync(directory, { recursive: true, force: true });
    }
});

test('hybrid route preserves observed semantic identities but excludes program shortcuts', async () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'benchmark-hybrid-meter-test-'));
    const gatewayFile = path.join(directory, 'gateway.mjs');
    fs.writeFileSync(gatewayFile, `import readline from 'node:readline';
readline.createInterface({input:process.stdin}).on('line',line=>{
const frame=JSON.parse(line);
const result=frame.method==='tools/list'?{tools:['get_ui_tree','click_element','cartridge_call'].map(name=>
({name,inputSchema:{type:'object',properties:{id:{type:'integer'}}}}))}:
{content:[{type:'text',text:'observed button'}],structuredContent:{success:true,snapshot_id:'fresh',id:23,name:'Save'}};
process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:frame.id,result})+'\\n');
});`);
    const { hybridUiTools } = await import('./browser-workbench/hybrid-ui.mjs');
    const configurationFile = path.join(directory, 'config.json');
    const metricsFile = path.join(directory, 'metrics.json');
    fs.writeFileSync(configurationFile, JSON.stringify({ command: process.execPath, arguments: [gatewayFile],
        route: 'hybrid-ui', metricsFile, tools: hybridUiTools }));
    const meter = spawn(process.execPath, ['modules/efficiency-benchmark/mcp-meter.mjs', configurationFile]);
    const waiting = new Map();
    readline.createInterface({ input: meter.stdout }).on('line', line => {
        const response = JSON.parse(line);
        waiting.get(response.id)?.(response);
    });
    function request(id, method, params = {}) {
        return new Promise(resolve => {
            waiting.set(id, resolve);
            meter.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
        });
    }
    try {
        const catalog = await request(1, 'tools/list');
        assert.deepEqual(catalog.result.tools.map(tool => tool.name), ['get_ui_tree', 'click_element']);
        const clicked = await request(2, 'tools/call', { name: 'click_element', arguments: { id: 23, snapshot_id: 'fresh' } });
        assert.equal(clicked.result.structuredContent.id, 23);
        assert.equal(clicked.result.structuredContent.snapshot_id, 'fresh');
        const refused = await request(3, 'tools/call', { name: 'cartridge_call', arguments: {} });
        assert.equal(refused.error.code, -32602);
    } finally {
        meter.stdin.end();
        await new Promise(resolve => meter.once('close', resolve));
        fs.rmSync(directory, { recursive: true, force: true });
    }
});
