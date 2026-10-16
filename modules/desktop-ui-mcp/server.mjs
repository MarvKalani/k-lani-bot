import fs from 'node:fs';
import readline from 'node:readline';
import { McpClient } from '../efficiency-benchmark/mcp-client.mjs';
import { DesktopUiGateway, compactInstructions } from './gateway.mjs';
import { uiTools, generatedCartridgeTools } from './protocol.mjs';
import { VisualTopic, visualTools } from './visual-topic.mjs';
import { CompatibilityFixtureClient } from './compatibility-fixtures.mjs';

// A serial MCP frontend keeps UI transitions ordered; a batch never runs in parallel with a new observation.
const configuration = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const transport = new McpClient(configuration.command, configuration.arguments);
const client = configuration.compatibilityFixture ? new CompatibilityFixtureClient(transport, configuration.compatibilityFixture) : transport;
const gateway = new DesktopUiGateway(client, entry => {
    if (configuration.auditFile) fs.appendFileSync(configuration.auditFile, JSON.stringify(entry) + '\n', { mode: 0o600 });
}, configuration.learning ?? null, { workflow: configuration.workflow, compatibility: configuration.compatibility });
gateway.serverInfo = await client.connect();
const visual = configuration.visual ? new VisualTopic(gateway, configuration.visual) : null;
let queue = Promise.resolve();
let closing = false;

async function handle(request) {
    if (request.id === undefined) return;
    let result;
    if (request.method === 'initialize') result = { protocolVersion: '2025-06-18', capabilities: { tools: {} },
        serverInfo: { name: 'k-lani-bot-desktop-ui', version: '1.5.0' }, instructions: compactInstructions };
    else if (request.method === 'tools/list') result = { tools: [...(visual ? visualTools : uiTools),
        ...generatedCartridgeTools] };
    else if (request.method === 'ping') result = {};
    else if (request.method === 'tools/call') {
        try {
            if (visual && request.params.name === 'cartridge_call') visual.current = null;
            const value = visual && request.params.name.startsWith('screen_')
                ? await visual.call(request.params.name, request.params.arguments ?? {})
                : await gateway.call(request.params.name, request.params.arguments ?? {});
            const images = value.images ?? [];
            delete value.images;
            result = { content: [{ type: 'text', text: JSON.stringify(value) }, ...images], isError: value.success === false };
        } catch (error) {
            result = { content: [{ type: 'text', text: JSON.stringify({ success: false, error: error.message }) }], isError: true };
        }
    } else {
        process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id,
            error: { code: -32601, message: 'Unsupported MCP method.' } }) + '\n');
        return;
    }
    process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, result }) + '\n');
}

async function shutdown() {
    if (closing) return;
    closing = true;
    await queue;
    let timeout;
    try {
        await Promise.race([client.call('end_session'), new Promise((resolve, reject) => {
            timeout = setTimeout(() => reject(new Error('Session cleanup timed out.')), 5000);
        })]);
    } catch (error) {
        process.stderr.write(error.message + '\n');
    } finally {
        clearTimeout(timeout);
        gateway.disconnect();
        client.close();
    }
}

readline.createInterface({ input: process.stdin }).on('line', line => {
    queue = queue.then(() => handle(JSON.parse(line))).catch(error => process.stderr.write(error.message + '\n'));
}).on('close', shutdown);
process.on('SIGTERM', shutdown);
