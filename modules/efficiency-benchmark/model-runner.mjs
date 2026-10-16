import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { spawn } from 'node:child_process';
import { performance } from 'node:perf_hooks';

// Measurement runs use fresh equivalent model contexts and a metered tool endpoint; raw events remain private.
// Usage is reported by Codex rather than estimated from bytes, and missing provider breakdowns remain unknown.
export async function runModel(configuration, server, prompt, directory) {
    fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
    const modelDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-bot-token-model-'));
    const meterFile = path.join(directory, 'meter.json');
    const metricsFile = path.join(directory, 'metrics.json');
    fs.writeFileSync(meterFile, JSON.stringify({ ...server, metricsFile }), { mode: 0o600 });
    const argumentsForModel = modelArguments(configuration, modelDirectory, meterFile, directory);
    const events = fs.createWriteStream(path.join(directory, 'events.jsonl'), { mode: 0o600 });
    const errors = fs.createWriteStream(path.join(directory, 'errors.log'), { mode: 0o600 });
    const model = spawn(configuration.codexExecutable, argumentsForModel, { stdio: ['pipe', 'pipe', 'pipe'] });
    const result = { usage: null, unexpectedAction: false, clientToolFailures: 0 };
    model.stdout.on('data', data => events.write(data));
    model.stderr.on('data', data => errors.write(data));
    readline.createInterface({ input: model.stdout }).on('line', line => observeEvent(line, result));
    const started = performance.now();
    model.stdin.end(prompt);
    const timeout = setTimeout(() => model.kill('SIGTERM'), configuration.timeoutMs ?? 300000);
    const exitCode = await new Promise((resolve, reject) => { model.once('error', reject); model.once('close', resolve); });
    clearTimeout(timeout);
    await Promise.all([new Promise(resolve => events.end(resolve)), new Promise(resolve => errors.end(resolve))]);
    fs.rmSync(modelDirectory, { recursive: true, force: true });
    const answerFile = path.join(directory, 'answer.json');
    const measurement = { ...result, exitCode, elapsedMs: performance.now() - started,
        answer: fs.existsSync(answerFile) ? JSON.parse(fs.readFileSync(answerFile, 'utf8')) : null,
        metrics: fs.existsSync(metricsFile) ? JSON.parse(fs.readFileSync(metricsFile, 'utf8')) : null };
    fs.writeFileSync(path.join(directory, 'model-result.json'), JSON.stringify(measurement), { mode: 0o600 });
    return measurement;
}

function modelArguments(configuration, directory, meterFile, output) {
    const schemaFile = path.join(output, 'answer-schema.json');
    const answerSchema = configuration.answerSchema ?? { type: 'object', properties: {
        completed: { type: 'boolean' }, total: { type: 'string' }, path: { type: 'string' }, detail: { type: 'string' } },
        required: ['completed', 'total', 'path', 'detail'], additionalProperties: false };
    fs.writeFileSync(schemaFile, JSON.stringify(answerSchema), { mode: 0o600 });
    return ['exec', '--ignore-user-config', '--ephemeral', '--skip-git-repo-check', '--sandbox', 'read-only',
        '--disable', 'shell_tool', '--disable', 'apps', '--disable', 'skill_search', '-m', configuration.model,
        '-c', `model_reasoning_effort=${JSON.stringify(configuration.reasoningEffort)}`, '-C', directory,
        '-c', 'mcp_servers.benchmark.command="node"',
        '-c', `mcp_servers.benchmark.args=${JSON.stringify([path.resolve('modules/efficiency-benchmark/mcp-meter.mjs'), meterFile])}`,
        '-c', 'mcp_servers.benchmark.required=true', '-c', 'mcp_servers.benchmark.startup_timeout_sec=120',
        '-c', 'mcp_servers.benchmark.default_tools_approval_mode="approve"',
        '--json', '--output-schema', schemaFile, '-o', path.join(output, 'answer.json'), '-'];
}

function observeEvent(line, result) {
    const event = JSON.parse(line);
    if (event.type === 'turn.completed') result.usage = event.usage;
    if (event.item?.type === 'mcp_tool_call' && event.type === 'item.completed' && event.item.error) result.clientToolFailures++;
    if (event.item && !['agent_message', 'reasoning', 'mcp_tool_call', 'error', 'plan_update'].includes(event.item.type)) {
        result.unexpectedAction = true;
    }
}
