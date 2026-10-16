import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { spawn, spawnSync } from 'node:child_process';
import { McpClient } from '../efficiency-benchmark/mcp-client.mjs';

// Private acceptance drives the real GTK door through the public MCP routes and independently reads the app's saved output.
if (process.env.K_LANI_BOT_PRIVATE_GUI_TEST !== '1' || process.env.DISPLAY !== process.env.K_LANI_BOT_PRIVATE_DISPLAY
    || process.env.WAYLAND_DISPLAY) throw new Error('Use scripts/private-gui-session.sh.');
const configuration = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
for (const command of ['openbox', 'wmctrl', 'xdotool']) {
    const available = spawnSync('which', [command], { encoding: 'utf8' });
    if (available.status !== 0) throw new Error('Private native form acceptance requires installed helper: ' + command);
}
const root = path.resolve(process.argv[3]);
if (!root.startsWith(path.resolve('.artifacts') + path.sep)) throw new Error('Private acceptance state must stay inside .artifacts.');
fs.mkdirSync(root, { recursive: false, mode: 0o700 });
const listener = net.createServer();
await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
const port = listener.address().port;
await new Promise(resolve => listener.close(resolve));
const state = path.join(root, 'node-state');
const initialized = spawnSync(configuration.nodeExecutable, ['--init', '--workspace', path.join(root, 'workspace'),
    '--state', state, '--node', 'form-native', '--port', String(port), '--scopes', 'observe,ui',
    '--consent-mode', 'unattended', '--ui-guard', 'desktop'], { encoding: 'utf8' });
if (initialized.status !== 0) throw new Error('Private form node initialization failed.');
const log = fs.openSync(path.join(root, 'native.log'), 'w', 0o600);
const manager = spawn('openbox', ['--sm-disable'], { stdio: ['ignore', log, log] });
const supervisor = spawn(configuration.nodeExecutable, ['--serve', '--config', path.join(state, 'node.json')], { stdio: ['ignore', log, log] });
const output = path.join(root, 'workspace/result.json');
const application = spawn(configuration.fixtureExecutable, ['door-form', output], { stdio: ['ignore', log, log] });
const endpoint = path.join(root, 'gateway.json');
fs.writeFileSync(endpoint, JSON.stringify({ command: configuration.nodeExecutable,
    arguments: ['--mcp', '--nodes', path.join(state, 'nodes.json'), '--default-node', 'form-native'] }), { mode: 0o600 });
const client = new McpClient(process.execPath, [path.resolve('modules/desktop-ui-mcp/server.mjs'), endpoint]);
let passed = 0;

async function call(name, args = {}) {
    let timer;
    try {
        const response = await Promise.race([client.call(name, args), new Promise((resolve, reject) => {
            timer = setTimeout(() => reject(new Error('Private MCP operation timed out.')), 8000);
        })]);
        const value = JSON.parse(response.result?.content?.find(part => part.type === 'text')?.text ?? '{}');
        if (response.error || response.result?.isError || !value.success) throw new Error(value.error ?? response.error?.message ?? 'MCP operation failed.');
        return value;
    } finally {
        clearTimeout(timer);
    }
}

function check(condition, message) {
    if (!condition) throw new Error(message);
    passed++;
}

async function findApplication() {
    for (let attempt = 0; attempt < 30; attempt++) {
        try {
            const info = await call('ui_info');
            const window = info.windows.find(window => window.title === 'k-lani-bot · Synthetic door configurator');
            if (window) return window.pid;
        } catch (error) {
            if (!error.message.includes('MCP operation')) throw error;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('Real native door application is unavailable.');
}

async function formCall(name, args) {
    return call('cartridge_call', { function: name, arguments: args });
}

async function select(pid, scope, name, label) {
    const form = await formCall('form.read', { pid, scope });
    const field = form.fields.find(field => field.name === name);
    check(Boolean(field), 'Native field was not discovered: ' + name);
    const options = await formCall('form.options', { form_ref: form.form_ref, field_ref: field.field_ref, context_rev: form.context_rev });
    const option = options.options.find(option => option.label === label);
    check(Boolean(option), 'Native requested option is missing: ' + label);
    const result = await formCall('form.set', { form_ref: form.form_ref, field_ref: field.field_ref, context_rev: options.context_rev,
        options_rev: options.options_rev, option_id: option.option_id });
    check(result.verified, 'Native form set is unconfirmed.');
}

try {
    await client.connect();
    const pid = await findApplication();
    const view = await call('ui_observe', { pid });
    const scope = view.elements.find(element => element.role === 'Window').ref;
    const catalog = await call('cartridge_browse', { topic: 'Use/Forms' });
    check(catalog.functions.length === 4, 'Four routed functions must be discoverable.');
    const initial = await formCall('form.read', { pid, scope });
    check((await formCall('form.validate', { form_ref: initial.form_ref, context_rev: initial.context_rev })).ready,
        'Basic/900 must validate without hidden glass.');
    for (const [name, label] of [['Door model', 'Wide'], ['Door width', '1200 mm'], ['Side panels', 'Both'], ['Left glass', 'Security']]) {
        await select(pid, scope, name, label);
    }
    const final = await formCall('form.read', { pid, scope });
    check((await formCall('form.validate', { form_ref: final.form_ref, context_rev: final.context_rev })).ready, 'Final door is not valid.');
    const buttons = await call('ui_observe', { pid, full: true });
    const save = buttons.elements.find(element => element.name === 'Save door' && element.role === 'Button');
    check((await call('ui_act', { pid, steps: [{ action: 'click', ref: save.ref }] })).success, 'Application save failed.');
    const actual = JSON.parse(fs.readFileSync(output, 'utf8'));
    check(actual.Model === 'Wide' && actual.WidthMm === 1200 && actual.SidePanels === 'Both' && actual.LeftGlass === 'Security',
        'Independent application output differs from the requested configuration.');
    fs.writeFileSync(path.join(root, 'report.json'), JSON.stringify({ success: true, checks: passed, coordinates_used: false, model_runs: 0 }) + '\n');
    process.stdout.write(`${passed} real native form MCP checks passed.\n`);
} finally {
    client.close();
    for (const process of [application, supervisor, manager]) process.kill('SIGTERM');
    fs.closeSync(log);
}
