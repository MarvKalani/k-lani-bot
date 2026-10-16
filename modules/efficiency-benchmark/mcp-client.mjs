import readline from 'node:readline';
import { spawn } from 'node:child_process';

// The benchmark harness verifies prerequisites through the same MCP connection, independently of model perception.
// These setup calls are outside routine measurements and cannot expose their results to the comparison model.
export class McpClient {
    constructor(command, argumentsForCommand) {
        this.process = spawn(command, argumentsForCommand, { stdio: ['pipe', 'pipe', 'pipe'] });
        this.pending = new Map();
        this.nextId = 1;
        this.errors = '';
        this.process.stderr.on('data', data => { this.errors += data.toString(); });
        readline.createInterface({ input: this.process.stdout }).on('line', line => {
            const response = JSON.parse(line);
            const handler = this.pending.get(response.id);
            if (!handler) return;
            this.pending.delete(response.id);
            handler.resolve(response);
        });
        this.process.on('exit', () => {
            for (const handler of this.pending.values()) handler.reject(new Error('MCP preparation process exited.'));
        });
    }

    async connect() {
        const initialized = await this.request('initialize', { protocolVersion: '2025-06-18', capabilities: {},
            clientInfo: { name: 'benchmark-setup', version: '1.0.0' } });
        this.process.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
        this.protocolVersion = initialized.result?.protocolVersion ?? null;
        return initialized.result?.serverInfo ?? null;
    }

    request(method, params) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            this.process.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
        });
    }

    call(name, argumentsForTool = {}) {
        return this.request('tools/call', { name, arguments: argumentsForTool });
    }

    close() {
        this.process.stdin.end();
    }
}
