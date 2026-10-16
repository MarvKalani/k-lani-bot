import fs from 'node:fs';
import readline from 'node:readline';
import { McpClient } from '../efficiency-benchmark/mcp-client.mjs';
import { CompatibilityFixtureClient } from './compatibility-fixtures.mjs';

// A shared isolated-test proxy applies the same labelled metadata fixture to old and current frontends without changing app outputs.
const file = process.argv[2];
const metadata = fs.lstatSync(file);
if (!metadata.isFile() || metadata.size > 65536 || (metadata.mode & 0o077) !== 0) throw new Error('Use a private bounded fixture configuration.');
const configuration = JSON.parse(fs.readFileSync(file, 'utf8'));
if (configuration.isolation !== 'synthetic_private_lab' || typeof configuration.command !== 'string'
    || !Array.isArray(configuration.arguments)) throw new Error('This proxy is only configured for synthetic private lab acceptance.');
const client = new CompatibilityFixtureClient(new McpClient(configuration.command, configuration.arguments), configuration.profile);
const serverInfo = await client.connect();
let queue = Promise.resolve();
let closing = false;

async function handle(request) {
    if (request.id === undefined) return;
    let result;
    if (request.method === 'initialize') {
        result = { protocolVersion: client.protocolVersion, capabilities: { tools: {} }, serverInfo };
    } else if (request.method === 'tools/list') {
        const response = await client.request('tools/list', {});
        result = response.result;
    } else if (request.method === 'tools/call') {
        const response = await client.call(request.params.name, request.params.arguments ?? {});
        result = response.result;
        if (response.error) throw new Error(response.error.message);
    } else if (request.method === 'ping') {
        result = {};
    } else {
        throw new Error('Unsupported synthetic fixture MCP method.');
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
            timeout = setTimeout(() => reject(new Error('Fixture session cleanup timed out.')), 5000);
        })]);
    } catch (error) {
        process.stderr.write(error.message + '\n');
    } finally {
        clearTimeout(timeout);
        client.close();
    }
}

readline.createInterface({ input: process.stdin }).on('line', line => {
    const request = JSON.parse(line);
    queue = queue.then(() => handle(request)).catch(error => {
        if (request.id !== undefined) process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id,
            error: { code: -32603, message: error.message } }) + '\n');
    });
}).on('close', shutdown);
process.on('SIGTERM', shutdown);
