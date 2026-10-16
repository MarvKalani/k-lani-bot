import fs from 'node:fs';
import path from 'node:path';
import { assessCompatibility, captureCompatibility, contractHash, schemaHashes } from './compatibility.mjs';
import { CompatibilityEvidence } from './compatibility-evidence.mjs';

export const compatibilityDefinitions = [
    { name: 'Info.compatibility', topic: 'Info/Compatibility',
        description: 'Read-only compatibility assessment for one function, with current native contracts and private evidence. No input.',
        inputSchema: { type: 'object', properties: { function: { type: 'string' } }, required: ['function'], additionalProperties: false } },
    { name: 'Programming.revalidate', topic: 'Programming/Compatibility',
        description: 'Accept a matching trusted isolated acceptance receipt and atomically replace the active revision with CAS. '
            + 'Caller-asserted success is not evidence; changed writers require an independent functional test.',
        inputSchema: { type: 'object', properties: { function: { type: 'string' }, expected_revision: { type: 'integer', minimum: 1 },
            evidence_ref: { type: 'string' } }, required: ['function', 'expected_revision', 'evidence_ref'], additionalProperties: false } }
];

// The configured host reads actual node contracts, assesses one function and consumes only separately issued acceptance evidence.
export class CompatibilityRuntime {
    constructor(gateway, configuration = null) {
        this.gateway = gateway;
        this.capabilities = {};
        this.tools = null;
        this.gatewayDigest = gatewayImplementationHash();
        this.configuration = configuration;
        if (configuration && gateway.learned) {
            const catalog = path.resolve(path.dirname(gateway.learned.file), '../../..');
            const evidenceRoot = path.resolve(configuration.evidenceRoot);
            if (evidenceRoot === catalog || evidenceRoot.startsWith(catalog + path.sep)) {
                throw new Error('Private acceptance evidence must remain outside cartridge exports.');
            }
        }
        this.evidence = configuration ? new CompatibilityEvidence(configuration) : null;
    }

    async observe(capabilities) {
        this.capabilities = capabilities;
        if (typeof this.gateway.client.request === 'function') {
            const result = await this.gateway.client.request('tools/list', {});
            if (result.error || !Array.isArray(result.result?.tools)) throw new Error('Native action schema probe failed.');
            this.tools = schemaHashes(result.result.tools);
        }
    }

    current() {
        const gateway = this.gateway;
        const application = gateway.learned?.application;
        const nativeWindows = gateway.applicationWindows.filter(window => window.process_name === application?.process_name);
        const versions = new Set(nativeWindows.map(window => window.version));
        const build = application?.identity_source === 'native-version'
            ? versions.size === 1 ? [...versions][0] : 'unknown' : application?.version;
        const schemas = this.tools && Object.fromEntries(Object.entries(this.tools).filter(([name]) =>
            !Array.isArray(this.capabilities.activeTools) || this.capabilities.activeTools.includes(name)));
        return { node_version: this.capabilities.version ?? gateway.serverInfo?.version,
            wire_version: gateway.client.protocolVersion ?? null, platform: this.capabilities.platform ?? gateway.platform,
            application: { key: application?.key, version: build }, action_schemas: schemas,
            native_implementation: this.capabilities.nativeImplementationSha256 ?? null,
            gateway_implementation: this.gatewayDigest };
    }

    capture(entry) {
        const current = this.current();
        if (!current.wire_version || !current.native_implementation || !current.action_schemas) {
            return { node_version: current.node_version, platform: current.platform, application_version: current.application.version };
        }
        return captureCompatibility(entry, current);
    }

    assess(entry) {
        return assessCompatibility(entry, this.current());
    }

    async info(args) {
        validateCompatibilityArguments('Info.compatibility', args);
        this.gateway.learned.functions = this.gateway.learned.load();
        const entry = this.gateway.learned.get(args.function);
        await this.gateway.info();
        const assessment = this.assess(entry);
        const evidence = this.gateway.results.put(this.gateway.resultNode, {}, JSON.stringify({
            function: entry.name, assessment, recorded: entry.compatibility, observed: this.current(),
            acceptance_receipt: false }));
        return { success: true, function: entry.name, revision: this.gateway.learned.generation,
            ...assessment, evidence_ref: evidence,
            ...(entry.compatibility.acceptance_ref ? { acceptance_ref: entry.compatibility.acceptance_ref } : {}) };
    }

    async revalidate(args) {
        validateCompatibilityArguments('Programming.revalidate', args);
        if (!this.evidence) throw new Error('Configure private evidence issued by a reviewed isolated acceptance runner.');
        this.gateway.learned.functions = this.gateway.learned.load();
        if (this.gateway.learned.generation !== args.expected_revision) throw new Error('stale_cartridge_revision');
        const entry = this.gateway.learned.get(args.function);
        await this.gateway.info();
        if (this.capabilities.nodeId !== this.configuration.authority.node) throw new Error('Acceptance target node authority changed.');
        if (this.assess(entry).status === 'incompatible') throw new Error('Incompatible function scope cannot be widened by revalidation.');
        const compatibility = { ...this.evidence.read(args.evidence_ref, entry, this.current()), acceptance_ref: args.evidence_ref };
        const functions = this.gateway.learned.functions.map(functionEntry => functionEntry.name === entry.name
            ? { ...functionEntry, compatibility } : functionEntry);
        this.gateway.learned.save(functions, this.gateway.learned.candidates);
        return { success: true, function: entry.name, status: 'reusable', revision: this.gateway.learned.generation,
            evidence_ref: args.evidence_ref, stored_values: false };
    }
}

export function validateCompatibilityArguments(name, args) {
    const schema = compatibilityDefinitions.find(entry => entry.name === name)?.inputSchema;
    if (!schema || !args || typeof args !== 'object' || Array.isArray(args)
        || Object.keys(args).some(key => !Object.hasOwn(schema.properties, key))
        || schema.required.some(key => !Object.hasOwn(args, key))) throw new Error('Invalid compatibility arguments.');
    for (const [key, value] of Object.entries(args)) {
        if (schema.properties[key].type === 'string' && (typeof value !== 'string' || value.length < 1 || value.length > 128)
            || schema.properties[key].type === 'integer' && (!Number.isSafeInteger(value) || value < 1)) {
            throw new Error('Invalid compatibility argument: ' + key);
        }
    }
}

function gatewayImplementationHash() {
    const directory = import.meta.dirname;
    const files = fs.readdirSync(directory).filter(file => file.endsWith('.mjs') && !file.endsWith('.test.mjs')).sort();
    return contractHash(files.map(file => ({ file, digest: contractHash(fs.readFileSync(path.join(directory, file), 'utf8')) })));
}
