import fs from 'node:fs';
import path from 'node:path';
import { createHmac, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { assessCompatibility, captureCompatibility, contractHash, functionScope } from './compatibility.mjs';

// Reviewed isolated acceptance code issues signed private receipts; MCP callers may only reference matching receipts.
export class CompatibilityEvidence {
    constructor(configuration) {
        const identity = configuration?.authority;
        if (!configuration?.evidenceRoot || ['node', 'principal', 'connection'].some(name => typeof identity?.[name] !== 'string' || !identity[name])) {
            throw new Error('Configure an authorized private acceptance evidence store.');
        }
        this.root = path.resolve(configuration.evidenceRoot);
        this.authority = contractHash({ node: identity.node, principal: identity.principal, connection: identity.connection });
        fs.mkdirSync(this.root, { recursive: true, mode: 0o700 });
        if (fs.lstatSync(this.root).isSymbolicLink()) throw new Error('Acceptance evidence rejects symbolic links.');
        fs.chmodSync(this.root, 0o700);
        const key = path.join(this.root, 'receipt-signing.key');
        if (!exists(key)) fs.writeFileSync(key, randomBytes(32), { flag: 'wx', mode: 0o600 });
        requirePrivateFile(key, 32);
        this.key = fs.readFileSync(key);
        if (this.key.length !== 32) throw new Error('Invalid receipt signing key.');
    }

    record(entry, current, proof) {
        if (!['functional', 'read_only'].includes(proof.test) || proof.test !== 'functional' && entry.steps.some(step => step.action !== 'read')
            || !['private_virtual_desktop', 'private_sandbox'].includes(proof.isolation?.kind)
            || !proof.isolation.node || proof.isolation.purpose !== 'synthetic_acceptance'
            || proof.verified !== true || !/^[a-f0-9]{64}$/.test(proof.independent_oracle_digest)
            || !/^[a-f0-9]{64}$/.test(proof.execution_digest)) throw new Error('Matching isolated functional acceptance is required.');
        const reference = randomUUID();
        const payload = { format: 'k-lani-bot-compatibility-acceptance/1', authority: this.authority,
            function_scope: functionScope(entry), previous_compatibility: contractHash(entry.compatibility),
            compatibility: captureCompatibility(entry, current), proof: structuredClone(proof), created_at: new Date().toISOString() };
        const record = { payload, signature: this.sign(payload) };
        const file = path.join(this.root, reference + '.json');
        const descriptor = fs.openSync(file, 'wx', 0o600);
        try {
            fs.writeFileSync(descriptor, JSON.stringify(record));
            fs.fsyncSync(descriptor);
        } finally {
            fs.closeSync(descriptor);
        }
        return reference;
    }

    read(reference, entry, current) {
        if (!/^[a-f0-9-]{36}$/.test(reference)) throw new Error('unknown_acceptance_evidence');
        const file = path.join(this.root, reference + '.json');
        requirePrivateFile(file, 64 * 1024);
        const record = JSON.parse(fs.readFileSync(file, 'utf8'));
        const expected = Buffer.from(this.sign(record.payload), 'hex');
        const supplied = typeof record.signature === 'string' && /^[a-f0-9]{64}$/.test(record.signature)
            ? Buffer.from(record.signature, 'hex') : Buffer.alloc(0);
        if (supplied.length !== expected.length || !timingSafeEqual(supplied, expected)) throw new Error('forged_acceptance_evidence');
        const payload = record.payload;
        if (payload.format !== 'k-lani-bot-compatibility-acceptance/1' || payload.authority !== this.authority
            || payload.function_scope !== functionScope(entry) || payload.previous_compatibility !== contractHash(entry.compatibility)) {
            throw new Error('foreign_or_stale_acceptance_evidence');
        }
        const reassessed = assessCompatibility({ ...entry, compatibility: payload.compatibility }, current);
        if (reassessed.status !== 'reusable') throw new Error('acceptance_scope_changed: ' + reassessed.reason);
        return structuredClone(payload.compatibility);
    }

    sign(payload) {
        return createHmac('sha256', this.key).update(contractHash(payload)).digest('hex');
    }
}

export async function runIsolatedAcceptance(gateway, evidence, name, parameters, isolation, independentOracle) {
    if (typeof independentOracle !== 'function' || isolation?.purpose !== 'synthetic_acceptance') {
        throw new Error('An independent host oracle and isolated synthetic acceptance scope are required.');
    }
    await gateway.info();
    if (gateway.compatibilityRuntime.capabilities.nodeId !== isolation.node) throw new Error('Isolated node identity differs from the approved target.');
    const application = gateway.learned.application;
    if (application.identity_source !== 'controlled-test-fixture' && application.key !== 'mousepad') {
        throw new Error('This acceptance driver operates controlled fixtures or isolated Mousepad test documents only.');
    }
    gateway.learned.functions = gateway.learned.load();
    const entry = gateway.learned.get(name);
    const current = gateway.compatibilityRuntime.current();
    if (assessCompatibility(entry, current).status === 'incompatible') throw new Error('Isolated acceptance cannot widen an incompatible application scope.');
    const candidate = { ...structuredClone(entry), compatibility: captureCompatibility(entry, current) };
    const kinds = new Set(entry.steps.map(step => step.selector.window));
    const pids = [...new Set(gateway.applicationWindows.filter(window => kinds.has(window.kind)).map(window => window.pid))];
    if (pids.length !== 1) throw new Error('Isolated acceptance requires one unambiguous application instance.');
    const response = await gateway.executeFunction(candidate, { pid: pids[0], parameters });
    if (!response.success || !response.verified) throw new Error('Isolated function did not verify its actual effect.');
    const observed = await independentOracle();
    if (!observed || observed.verified !== true || typeof observed.digest !== 'string') throw new Error('Independent oracle did not verify the actual application result.');
    await gateway.info();
    if (assessCompatibility(candidate, gateway.compatibilityRuntime.current()).status !== 'reusable') {
        throw new Error('Acceptance target contracts changed during the isolated test.');
    }
    const reference = evidence.record(entry, current, { test: entry.steps.some(step => step.action !== 'read') ? 'functional' : 'read_only',
        isolation, verified: true, independent_oracle_digest: observed.digest, execution_digest: contractHash(response) });
    return { success: true, function: name, evidence_ref: reference, accepted_effect: true };
}

function requirePrivateFile(file, maximumBytes) {
    if (!exists(file)) throw new Error('unknown_acceptance_evidence');
    const metadata = fs.lstatSync(file);
    if (!metadata.isFile() || metadata.size > maximumBytes || (metadata.mode & 0o077) !== 0) {
        throw new Error('Acceptance evidence must be private, bounded and nonsymbolic.');
    }
}

function exists(file) {
    try {
        fs.lstatSync(file);
        return true;
    } catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}
