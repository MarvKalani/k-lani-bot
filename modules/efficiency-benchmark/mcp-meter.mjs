import fs from 'node:fs';
import readline from 'node:readline';
import { spawn } from 'node:child_process';
import { performance } from 'node:perf_hooks';
import { ScreenshotTreatment } from './screenshot-treatment.mjs';

// A transparent MCP meter counts what reaches the benchmark model. The vision route exposes only screenshots and physical
// input; object identities, accessibility values and postcondition selectors cannot leak into that route.
const configuration = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const gateway = spawn(configuration.command, configuration.arguments,
    { stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, ...configuration.environment } });
const pending = new Map();
const metrics = { route: configuration.route, schemaBytes: 0, functionSchemaBytes: 0, calls: [] };
const treatment = configuration.imageTreatment ? new ScreenshotTreatment(configuration.imageTreatment) : null;
let lastDeliveredAt = performance.now();
let lastObservationAt = null;
let responseQueue = Promise.resolve();
const gatewayErrors = fs.createWriteStream(configuration.metricsFile + '.gateway-errors.log', { mode: 0o600 });
gateway.stderr.on('data', data => gatewayErrors.write(data));

function save() {
    fs.writeFileSync(configuration.metricsFile, JSON.stringify(metrics, null, 2), { mode: 0o600 });
}

function visionDefinition(tool) {
    const permitted = { take_screenshot: [], mouse_click: ['x', 'y', 'button', 'snapshot_id'],
        mouse_move: ['x', 'y', 'snapshot_id'], drag_and_drop: ['from_x', 'from_y', 'to_x', 'to_y', 'snapshot_id'],
        hotkey: ['keys', 'snapshot_id'], set_clipboard: ['text'],
        save_macro: ['name', 'description', 'steps'], run_macro: ['name', 'sha256', 'snapshot_id'] };
    const descriptions = {
        take_screenshot: 'Observe the private desktop as an image; use this for all reading and verification.',
        mouse_click: 'Click coordinates observed in a fresh screenshot. Only physical input is available.',
        mouse_move: 'Move the pointer to coordinates observed in a fresh screenshot.',
        drag_and_drop: 'Drag between two coordinates observed in a fresh screenshot, for example a scrollbar thumb.',
        hotkey: 'Press keys in the focused application, e.g. ["ctrl","a"], ["ctrl","v"], ["pagedown"], ["end"], '
            + '["home"], ["space"], ["down"], ["tab"], ["enter"].',
        set_clipboard: 'Set literal clipboard text for pasting into a focused field with Ctrl+V.',
        save_macro: 'Save a keyboard-only batch. Each step is {action:"hotkey",args:{keys:[...]}} or '
            + '{action:"set_clipboard",args:{text:"..."}}. No coordinates, DOM, selectors, screenshots or program APIs. Returns macro_name and macro_sha256.',
        run_macro: 'Execute the saved keyboard-only batch by name and sha256 with the node. Use after observing and focusing '
            + 'the intended field. Group typing and Tab navigation while layout is stable; observe afterward.'
    };
    const properties = Object.fromEntries(permitted[tool.name].map(key => [key, tool.inputSchema.properties[key]]));
    return { ...tool, description: descriptions[tool.name], inputSchema: { type: 'object', properties,
        required: (tool.inputSchema.required || []).filter(key => permitted[tool.name].includes(key)), additionalProperties: false } };
}

function visionResult(result) {
    let source = result.structuredContent;
    if (!source) {
        try { source = JSON.parse(result.content.find(part => part.type === 'text')?.text || '{}'); }
        catch { source = {}; }
    }
    const payload = { success: source.success, outcome: source.outcome, snapshot_id: source.snapshot_id,
        width: source.width, height: source.height, mime_type: source.mime_type, code: source.code,
        macro_name: source.macro?.name, macro_sha256: source.macro?.sha256,
        steps_completed: source.steps_completed, failed_step: source.failed_step };
    const images = (result.content || []).filter(part => part.type === 'image');
    return { content: [{ type: 'text', text: JSON.stringify(payload) }, ...images],
        structuredContent: payload, isError: result.isError };
}

function recordResult(request, response, imageEvidence) {
    const result = response.result || { content: [{ type: 'text', text: response.error?.message || 'MCP error' }], isError: true };
    let payload = result.structuredContent;
    if (!payload) {
        try { payload = JSON.parse(result.content.find(part => part.type === 'text')?.text || '{}'); }
        catch { payload = {}; }
    }
    const images = (result.content || []).filter(part => part.type === 'image');
    metrics.calls.push({ tool: request.tool, arguments: request.arguments, elapsedMs: performance.now() - request.started,
        decisionMs: request.decisionMs, observationToPositionMs: request.observationToPositionMs,
        physicalArguments: request.physicalArguments, imageEvidence,
        viewEvidence: payload?.source_pixels ? { sourcePixels: payload.source_pixels, deliveredPixels: payload.delivered_pixels,
            region: payload.region, markedElements: payload.marked_elements } : null,
        resultJsonBytes: Buffer.byteLength(JSON.stringify(response.result || response.error)),
        textBytes: (result.content || []).filter(part => part.type === 'text').reduce((sum, part) => sum + Buffer.byteLength(part.text), 0),
        imageCount: images.length, imageBytes: images.reduce((sum, part) => sum + Buffer.from(part.data, 'base64').length, 0),
        failed: result.isError === true || payload?.success === false || response.error !== undefined });
    const functionSchema = payload?.function?.inputSchema ?? payload?.inputSchema;
    if (functionSchema) metrics.functionSchemaBytes += Buffer.byteLength(JSON.stringify(functionSchema));
    save();
}

readline.createInterface({ input: process.stdin }).on('line', line => {
    const request = JSON.parse(line);
    if (request.method === 'tools/call') {
        const tool = request.params.name;
        const argumentsForTool = request.params.arguments || {};
        const keyboardMacro = tool === 'save_macro' && (!Array.isArray(argumentsForTool.steps)
            || argumentsForTool.steps.some(step => !['hotkey', 'set_clipboard'].includes(step.action)
                || Object.keys(step).some(key => !['action', 'args'].includes(key))
                || !step.args || Object.keys(step.args).some(key => !['keys', 'text'].includes(key))));
        if (!configuration.tools.includes(tool) || configuration.route === 'screenshot-adapter'
            && (keyboardMacro || Object.keys(argumentsForTool).some(key =>
                !['x', 'y', 'button', 'snapshot_id', 'keys', 'text', 'from_x', 'from_y', 'to_x', 'to_y',
                    'name', 'description', 'steps', 'sha256'].includes(key)))) {
            const refusal = { jsonrpc: '2.0', id: request.id,
                error: { code: -32602, message: 'Tool or argument is outside the measured route.' } };
            metrics.calls.push({ tool, arguments: argumentsForTool, elapsedMs: 0, resultJsonBytes: Buffer.byteLength(JSON.stringify(refusal)),
                textBytes: 0, imageCount: 0, imageBytes: 0, failed: true, rejected: true });
            save();
            process.stdout.write(JSON.stringify(refusal) + '\n');
            return;
        }
        let physicalArguments;
        try { physicalArguments = treatment?.mapArguments(tool, argumentsForTool) ?? argumentsForTool; }
        catch (error) {
            const refusal = { jsonrpc: '2.0', id: request.id, error: { code: -32602, message: error.message } };
            metrics.calls.push({ tool, arguments: argumentsForTool, elapsedMs: 0, resultJsonBytes: Buffer.byteLength(JSON.stringify(refusal)),
                textBytes: 0, imageCount: 0, imageBytes: 0, failed: true, rejected: true });
            save();
            process.stdout.write(JSON.stringify(refusal) + '\n');
            return;
        }
        const started = performance.now();
        pending.set(request.id, { method: request.method, tool, arguments: argumentsForTool, physicalArguments,
            started, decisionMs: started - lastDeliveredAt,
            observationToPositionMs: ['mouse_click', 'mouse_move', 'drag_and_drop'].includes(tool) && lastObservationAt !== null
                ? started - lastObservationAt : null });
        request.params.arguments = physicalArguments;
    } else if (request.id !== undefined) pending.set(request.id, { method: request.method });
    gateway.stdin.write(JSON.stringify(request) + '\n');
}).on('close', () => { save(); gateway.stdin.end(); });

async function forwardResult(line) {
    const response = JSON.parse(line);
    const request = pending.get(response.id);
    pending.delete(response.id);
    if (request?.method === 'initialize' && response.result?.serverInfo) {
        metrics.serverInfo = response.result.serverInfo;
        save();
    }
    if (request?.method === 'tools/list' && response.result?.tools) {
        response.result.tools = response.result.tools.filter(tool => configuration.tools.includes(tool.name));
        if (configuration.route === 'screenshot-adapter') response.result.tools = response.result.tools.map(visionDefinition);
        metrics.schemaBytes = Buffer.byteLength(JSON.stringify(response.result.tools));
        save();
    }
    if (request?.method === 'tools/call') {
        if (configuration.route === 'screenshot-adapter' && response.result) response.result = visionResult(response.result);
        const evidence = [];
        if (treatment && response.result) {
            for (let index = 0; index < response.result.content.length; index++) {
                const part = response.result.content[index];
                if (part.type !== 'image') continue;
                const transformed = await treatment.transform(part);
                response.result.content[index] = transformed.image;
                evidence.push(transformed.evidence);
            }
            if (evidence.length > 0) {
                response.result.structuredContent = { ...response.result.structuredContent,
                    width: treatment.geometry.width, height: treatment.geometry.height, coordinate_space: 'delivered_image_pixels',
                    cursor_included: configuration.imageTreatment.cursor === true };
                response.result.content[0] = { type: 'text', text: JSON.stringify(response.result.structuredContent) };
            }
        }
        recordResult(request, response, evidence);
    }
    process.stdout.write(JSON.stringify(response) + '\n');
    lastDeliveredAt = performance.now();
    if (request?.tool === 'take_screenshot') lastObservationAt = lastDeliveredAt;
}

readline.createInterface({ input: gateway.stdout }).on('line', line => {
    responseQueue = responseQueue.then(() => forwardResult(line));
});
gateway.on('exit', async code => { await responseQueue; save(); gatewayErrors.end(); process.exit(code || 0); });
process.on('SIGTERM', () => { save(); gateway.kill('SIGTERM'); });
