import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';
import { gatewayTools } from './workbench.mjs';
import { Comparison } from './comparison.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const resources = new Map([['/', ['index.html', 'text/html']], ['/app.mjs', ['app.mjs', 'text/javascript']]]);
resources.set('/comparison', ['index.html', 'text/html']);
const bodyLimit = 65536;

async function readJson(request) {
    let text = '';
    for await (const chunk of request) {
        text += chunk;
        if (Buffer.byteLength(text) > bodyLimit) throw new Error('Request exceeds 64 KiB.');
    }
    return JSON.parse(text);
}

function sendJson(response, status, value) {
    response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    response.end(JSON.stringify(value));
}

export function createServer(workbench, comparison = null) {
    return http.createServer(async (request, response) => {
        try {
            const origin = `http://${request.headers.host}`;
            if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(origin)
                || request.headers.origin && request.headers.origin !== origin) throw new Error('Foreign origin rejected.');
            const url = new URL(request.url, origin);
            const selected = comparison ? comparison.session(url.searchParams.get('session') ?? 'mcp') : workbench;
            if (request.method === 'GET' && url.pathname === '/state') {
                sendJson(response, 200, { ...selected.state(), task: comparison?.task() ?? null });
            } else if (request.method === 'GET' && url.pathname === '/comparison/result' && comparison) {
                sendJson(response, 200, await comparison.verify());
            } else if (request.method === 'GET' && resources.has(url.pathname)) {
                const [file, type] = resources.get(url.pathname);
                response.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-store',
                    'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'unsafe-inline'; frame-src 'self'" });
                response.end(await fs.readFile(path.join(directory, file)));
            } else if (request.method === 'POST' && url.pathname === '/mcp') {
                if (request.headers['content-type'] !== 'application/json') throw new Error('JSON requests only.');
                sendJson(response, 200, await handleRpc(await readJson(request), selected));
            } else {
                sendJson(response, 404, { error: 'Unknown route.' });
            }
        } catch (error) {
            sendJson(response, 400, { error: error.message });
        }
    });
}

export async function handleRpc(message, workbench) {
    const response = { jsonrpc: '2.0', id: message.id };
    try {
        if (message.method === 'initialize') response.result = { protocolVersion: '2025-06-18', capabilities: { tools: {} },
            serverInfo: { name: 'k-lani-bot-browser-workbench', version: '1.0.0' } };
        else if (message.method === 'tools/list') response.result = { tools: gatewayTools };
        else if (message.method === 'tools/call') {
            const result = await workbench.dispatch(message.params.name, message.params.arguments ?? {});
            response.result = { content: [{ type: 'text', text: JSON.stringify(result) }] };
        } else throw new Error('Unknown MCP method.');
    } catch (error) {
        response.error = { code: -32602, message: error.message };
    }
    return response;
}

async function runStdio(address) {
    const endpoint = new URL(address);
    if (endpoint.hostname !== '127.0.0.1' || endpoint.protocol !== 'http:') throw new Error('Loopback HTTP endpoint required.');
    endpoint.pathname = '/mcp';
    const lines = readline.createInterface({ input: process.stdin });
    for await (const line of lines) {
        let message;
        try {
            message = JSON.parse(line);
            if (message.id === undefined) continue;
            const response = await fetch(endpoint, { method: 'POST',
                headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(message) });
            if (!response.ok) throw new Error('Workbench endpoint rejected the request.');
            process.stdout.write(JSON.stringify(await response.json()) + '\n');
        } catch (error) {
            process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: message?.id ?? null,
                error: { code: -32603, message: error.message } }) + '\n');
        }
    }
}

async function main() {
    if (process.argv[2] === '--stdio') return runStdio(process.argv[3]);
    const root = process.argv[2];
    if (!root) throw new Error('Supply a disposable test output directory.');
    const comparison = new Comparison(root, Number(process.argv[3] ?? 0));
    const server = createServer(comparison.session('mcp'), comparison);
    server.listen(Number(process.env.K_LANI_BOT_BENCHMARK_PORT ?? 0), '127.0.0.1', () => {
        const origin = `http://127.0.0.1:${server.address().port}`;
        console.log(JSON.stringify({ url: `${origin}/comparison`, root: path.resolve(root),
            computerUseUrl: `${origin}/?session=computer-use`, mcpUrl: `${origin}/?session=mcp` }));
    });
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await main();
