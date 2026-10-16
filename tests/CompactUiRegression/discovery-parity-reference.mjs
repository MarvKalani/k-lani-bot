import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { FormScanner } from '../../modules/desktop-ui-mcp/form-scanner.mjs';
import { ResultStore } from '../../modules/desktop-ui-mcp/result-store.mjs';
import { formatResult } from '../../modules/desktop-ui-mcp/result-format.mjs';

// Host reference files contain only synthetic discovery structures. This does not operate or simulate an installed application.
export function discoveryFixtures(corpus) {
    return corpus.cases.map(fixture => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-discovery-reference-'));
        const learning = { file: path.join(directory, 'generated-ui.json'), application: { key: 'form-fixture', version: '1.0' } };
        let scanner = new FormScanner(learning);
        const replies = new Map();
        try {
            const steps = fixture.steps.map(step => {
                if (step.restart) scanner = new FormScanner(learning);
                const args = structuredClone(step.arguments);
                if (step.state_from) args.state = replies.get(step.state_from).state;
                try {
                    const nodes = step.nodes?.map(node => ({ depth: 0, autoId: '', value: null, enabled: true, offscreen: false,
                        ...node, value: node.value === undefined ? null : node.value?.repeat(node.repeat_value ?? 1) }));
                    const reply = step.action === 'read' ? scanner.read(args) : scanner.scan(nodes, args,
                        { node_version: 'fixture-node', platform: 'fixture-platform', application_version: '1.0' },
                        { WindowHandle: 100, ProcessStartedAtUtc: 'start', Kind: 'main' });
                    replies.set(step.name, reply);
                    let output = reply;
                    if (step.format) {
                        reply.success = true;
                        if (args.result_mode === 'values') {
                            reply.outputs = Object.fromEntries(reply.contents.fields.map(field => [field.key, field.value ?? field.name]));
                        }
                        output = formatResult(new ResultStore(), 'node', reply, args, {},
                            { field_count: reply.fields.length, content_count: reply.contents?.fields.length ?? 0 });
                    }
                    return { name: step.name, reply: normalize(output) };
                } catch (error) {
                    return { name: step.name, reply: { error: error.message } };
                }
            });
            return { name: fixture.name, steps,
                document: fs.existsSync(scanner.file) ? JSON.parse(fs.readFileSync(scanner.file, 'utf8')) : null };
        } finally {
            fs.rmSync(directory, { recursive: true, force: true });
        }
    });
}

function normalize(value) {
    if (Array.isArray(value)) return value.map(normalize);
    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([name, item]) => [name, name === 'evidence_ref' ? '$evidence' : normalize(item)]));
    }
    return value;
}
