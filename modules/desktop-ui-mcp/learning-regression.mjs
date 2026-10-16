import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { spawn, spawnSync } from 'node:child_process';
import { McpClient } from '../efficiency-benchmark/mcp-client.mjs';
import { Comparison } from '../efficiency-benchmark/browser-workbench/comparison.mjs';
import { createServer } from '../efficiency-benchmark/browser-workbench/server.mjs';

// Acceptance uses the real node and private browser. The only application operations are native MCP calls.
// A fresh browser process and MCP frontend must reuse generated selectors, not the original element IDs or input text.
if (process.env.K_LANI_BOT_PRIVATE_GUI_TEST !== '1' || process.env.DISPLAY !== process.env.K_LANI_BOT_PRIVATE_DISPLAY
    || process.env.WAYLAND_DISPLAY) throw new Error('Use scripts/private-gui-session.sh.');
const configuration = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const root = path.resolve(process.argv[3]);
fs.mkdirSync(root, { recursive: true, mode: 0o700 });
const comparison = new Comparison(path.join(root, 'fixture'));
const http = createServer(comparison.session('mcp'), comparison);
await new Promise(resolve => http.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${http.address().port}/?session=computer-use`;
const listener = net.createServer();
await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
const port = listener.address().port;
await new Promise(resolve => listener.close(resolve));
const state = path.join(root, 'node-state');
const initialized = spawnSync(configuration.nodeExecutable, ['--init', '--workspace', path.join(root, 'workspace'),
    '--state', state, '--node', 'learning', '--port', String(port), '--scopes', 'observe,ui,clipboard',
    '--consent-mode', 'unattended', '--ui-guard', 'desktop'], { encoding: 'utf8' });
if (initialized.status !== 0) throw new Error('Private node initialization failed.');
const log = fs.openSync(path.join(root, 'native.log'), 'w', 0o600);
const manager = spawn('openbox', ['--sm-disable'], { stdio: ['ignore', log, log] });
const node = spawn(configuration.nodeExecutable, ['--serve', '--config', path.join(state, 'node.json')], { stdio: ['ignore', log, log] });
const nodeArguments = ['--mcp', '--nodes', path.join(state, 'nodes.json'), '--default-node', 'learning'];
const endpoint = path.join(root, 'gateway.json');
fs.writeFileSync(endpoint, JSON.stringify({ command: configuration.nodeExecutable, arguments: nodeArguments,
    learning: { cartridgeRoot: path.join(root, 'cartridges'), auto_capture: configuration.automaticLearning === true,
        application: { key: 'browser-testwerkstatt', version: '1.1.0', default_topic: 'Use/Editor',
        identity_source: 'controlled-test-fixture' } } }), { mode: 0o600 });
let chrome;
let client;

async function call(name, args = {}) {
    const response = await client.call(name, args);
    const value = JSON.parse(response.result?.content?.find(part => part.type === 'text')?.text ?? '{}');
    if (response.error || response.result?.isError || value.success !== true) throw new Error(value.error ?? response.error?.message ?? 'MCP call failed.');
    return value;
}

async function connect() {
    client = new McpClient(process.execPath, [path.resolve('modules/desktop-ui-mcp/server.mjs'), endpoint]);
    await client.connect();
    for (let attempt = 0; attempt < 40; attempt++) {
        const info = await call('cartridge_info');
        const window = info.windows.find(window => window.title.includes('Browser-Testwerkstatt'));
        if (window) return window.pid;
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('Private application did not become accessible.');
}

function startBrowser(profile) {
    return spawn(configuration.chromeExecutable, ['--user-data-dir=' + path.join(root, profile), '--no-first-run',
        '--disable-background-networking', '--disable-dev-shm-usage', '--force-renderer-accessibility',
        '--force-device-scale-factor=1', '--kiosk', url], { stdio: ['ignore', log, log] });
}

async function learn(pid, name, steps, outputName, role, parameters, field) {
    const result = await call('ui_act', { pid, steps });
    if (configuration.automaticLearning) {
        // A generated name is not announced in the reply; the one function under the automatic topic is found by browsing it.
        const listed = await call('cartridge_browse', { topic: 'Use/UI', pid });
        if (listed.functions.length !== 1) throw new Error('First ordinary action did not create exactly one MCP function.');
        return { function: listed.functions[0].name };
    }
    const view = await call('ui_observe', { pid, full: true });
    const output = view.elements.find(element => element.role === role && element.name === outputName);
    if (!output) throw new Error('Expected demonstration output absent.');
    return call('cartridge_call', { function: 'Programming.learn', arguments: { evidence: result.evidence, name,
        topic: 'Use/Editor', parameters, outputs: [{ name: 'result', ref: output.ref, field }] } });
}

async function stopClient() {
    const closed = new Promise(resolve => client.process.once('close', resolve));
    client.close();
    await closed;
    client = null;
}

try {
    await new Promise(resolve => setTimeout(resolve, 1500));
    chrome = startBrowser('first-profile');
    const firstPid = await connect();
    const scanArguments = { pid: firstPid, form: 'workbench', contents: true, max_chars: 16000 };
    const scans = [];
    async function recordScan() {
        const compact = await call('ui_observe', { pid: firstPid, full: true, limit: 200, max_chars: 16000 });
        const scanned = await call('cartridge_call', { function: 'Info.scan_form', arguments: scanArguments });
        scans.push({ compact, scanned });
    }
    await recordScan();
    const firstView = await call('ui_observe', { pid: firstPid, full: true });
    const editorTab = firstView.elements.find(element => element.role === 'page tab' && element.name === 'Editor');
    const openedFunction = await learn(firstPid, 'editor.open', [{ action: 'click', ref: editorTab.ref }], 'Editor', 'heading', [], 'name');
    await recordScan();
    const editorView = await call('ui_observe', { pid: firstPid, full: true });
    const field = editorView.elements.find(element => element.role === 'Edit' && element.name === 'HTML-Text');
    const writeFunction = await learn(firstPid, 'editor.write_text', [{ action: 'write', ref: field.ref, text: 'First access: learn this function.' }],
        'HTML-Text', 'Edit', ['text'], 'value');
    await recordScan();
    fs.writeFileSync(path.join(root, 'form-observations.json'), JSON.stringify({ scope: 'Real private native MCP observations; synthetic contents only.', scans }),
        { mode: 0o600 });
    const hot = await call('cartridge_browse', { topic: 'Use/Editor' });
    if (hot.functions.length !== 2) throw new Error('Functions were not discovered without reload.');
    await stopClient();
    const stopped = new Promise(resolve => chrome.once('close', resolve));
    chrome.kill('SIGTERM');
    await stopped;
    chrome = startBrowser('second-profile');
    const secondPid = await connect();
    if (secondPid === firstPid) throw new Error('Acceptance requires a fresh browser process.');
    const recordedForm = await call('cartridge_call', { function: 'Info.form', arguments: { form: 'workbench' } });
    if (recordedForm.observed_states < 2 || JSON.stringify(recordedForm).includes('First access: learn this function.')) {
        throw new Error('Form structure must survive restart without retaining field values.');
    }
    const preflight = await call('cartridge_call', { function: 'Info.preflight', arguments: {
        pid: secondPid, function: openedFunction.function } });
    if (!preflight.ready || preflight.input_sent !== false) throw new Error('Read-only native preflight did not verify the learned target.');
    const opened = await call('cartridge_call', { function: openedFunction.function, arguments: { pid: secondPid, parameters: {} } });
    const reply = await call('cartridge_call', { function: writeFunction.function, arguments: {
        pid: secondPid, parameters: { text: 'Second access: direct MCP after restart.' } } });
    const output = configuration.automaticLearning ? reply.outputs.value : reply.outputs.result;
    if (!opened.verified || !reply.verified || output !== 'Second access: direct MCP after restart.') {
        throw new Error('Generated function did not verify the new value.');
    }
    fs.writeFileSync(path.join(root, 'acceptance.json'), JSON.stringify({ passed: true, freshBrowserProcess: true,
        frontendRestarted: true, firstUseAutomatic: Boolean(configuration.automaticLearning),
        readOnlyPreflight: preflight,
        formScanner: { observedStates: recordedForm.observed_states, survivesRestartWithoutValues: true },
        explicitLearningCalls: configuration.automaticLearning ? 0 : 2,
        hotDiscovery: hot.functions.map(entry => entry.name), directCall: reply,
        returnedBytes: Buffer.byteLength(JSON.stringify(reply)), modelImages: 0,
        tokenUsage: null, scope: 'Native MCP acceptance; no model token measurement performed.' }, null, 2), { mode: 0o600 });
    console.log('Auto-MCP acceptance passed: hot discovery, new frontend/browser, direct parameterized call and verified output.');
} finally {
    if (client) await stopClient();
    chrome?.kill('SIGTERM');
    node.kill('SIGTERM');
    manager.kill('SIGTERM');
    await new Promise(resolve => http.close(resolve));
    fs.closeSync(log);
}
