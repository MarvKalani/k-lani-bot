import fs from 'node:fs';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';

const maximumJobs = 100;
const maximumJobBytes = 1024 * 1024;
const jobReference = /^[0-9a-f]{64}$/;

// The gateway retains private job checkpoints separately from cartridges; native mutation deduplication remains in the node ledger.
export class WorkflowStore {
    constructor(root, authority) {
        if (!root || ['node', 'principal', 'connection'].some(name => typeof authority?.[name] !== 'string'
            || authority[name].length < 1 || authority[name].length > 256)) throw new Error('Configure private workflow state and authority.');
        this.root = path.resolve(root);
        const identity = { node: authority.node, principal: authority.principal, connection: authority.connection };
        this.authority = createHash('sha256').update(JSON.stringify(identity)).digest('hex');
        fs.mkdirSync(this.root, { recursive: true, mode: 0o700 });
        if (fs.lstatSync(this.root).isSymbolicLink()) throw new Error('Workflow state rejects symbolic links.');
        fs.chmodSync(this.root, 0o700);
        this.queues = new Map();
    }

    create(requestId, fingerprint, definition, parameters) {
        const lock = path.join(this.root, '.create.lock');
        this.recoverLock(lock);
        const descriptor = fs.openSync(lock, 'wx', 0o600);
        fs.writeFileSync(descriptor, JSON.stringify({ pid: process.pid, process_start: processStart(process.pid), authority: this.authority }));
        fs.fsyncSync(descriptor);
        try {
            return this.createLocked(requestId, fingerprint, definition, parameters);
        } finally {
            fs.closeSync(descriptor);
            fs.unlinkSync(lock);
        }
    }

    createLocked(requestId, fingerprint, definition, parameters) {
        if (typeof requestId !== 'string' || requestId.length < 1 || requestId.length > 128) throw new Error('Use a bounded client_request_id.');
        const reference = createHash('sha256').update(this.authority + '/' + requestId).digest('hex');
        const existing = this.filename(reference);
        if (fs.existsSync(existing)) {
            const job = this.read(reference);
            if (job.request_fingerprint !== fingerprint) throw new Error('client_request_id reused with different content.');
            return job;
        }
        const count = fs.readdirSync(this.root).filter(name => jobReference.test(name.slice(0, -5)) && name.endsWith('.json')).length;
        if (count >= maximumJobs) throw new Error('workflow_store_full');
        const job = { format: 'k-lani-bot-workflow-job/1', job_ref: reference, authority: this.authority,
            request_fingerprint: fingerprint, definition: structuredClone(definition), parameters: structuredClone(parameters),
            revision: 1, status: 'running', completed_stages: [], outputs: {}, pending: null, applications: {},
            created_at: new Date().toISOString(), updated_at: new Date().toISOString() };
        try {
            this.write(job, true);
        } catch (error) {
            if (error.code !== 'EEXIST') throw error;
            const stored = this.read(reference);
            if (stored.request_fingerprint !== fingerprint) throw new Error('client_request_id reused with different content.');
            return stored;
        }
        return job;
    }

    filename(reference) {
        if (!jobReference.test(reference)) throw new Error('unknown_job');
        return path.join(this.root, reference + '.json');
    }

    read(reference) {
        const file = this.filename(reference);
        if (!fs.existsSync(file) || fs.lstatSync(file).isSymbolicLink() || fs.statSync(file).size > maximumJobBytes) throw new Error('unknown_job');
        const job = JSON.parse(fs.readFileSync(file, 'utf8'));
        if (job.format !== 'k-lani-bot-workflow-job/1' || job.authority !== this.authority || job.job_ref !== reference) throw new Error('foreign_or_invalid_job');
        return job;
    }

    write(job, initial = false) {
        const file = this.filename(job.job_ref);
        if (job.authority !== this.authority) throw new Error('foreign_job');
        if (!initial) job.revision++;
        job.updated_at = new Date().toISOString();
        const text = JSON.stringify(job);
        if (Buffer.byteLength(text) > maximumJobBytes) throw new Error('workflow_job_too_large');
        if (fs.existsSync(file) && fs.lstatSync(file).isSymbolicLink()) throw new Error('Workflow checkpoints reject symbolic links.');
        const temporary = file + '.' + randomUUID() + '.tmp';
        const descriptor = fs.openSync(temporary, 'wx', 0o600);
        try {
            fs.writeFileSync(descriptor, text);
            fs.fsyncSync(descriptor);
        } finally {
            fs.closeSync(descriptor);
        }
        try {
            if (initial) fs.linkSync(temporary, file);
            else fs.renameSync(temporary, file);
            const directory = fs.openSync(this.root, 'r');
            try { fs.fsyncSync(directory); } finally { fs.closeSync(directory); }
        } finally {
            if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
        }
    }

    async serialized(reference, action) {
        const previous = this.queues.get(reference) ?? Promise.resolve();
        const current = previous.catch(() => {}).then(() => this.locked(reference, action));
        this.queues.set(reference, current);
        try {
            return await current;
        } finally {
            if (this.queues.get(reference) === current) this.queues.delete(reference);
        }
    }

    async locked(reference, action) {
        const lock = this.filename(reference) + '.lock';
        this.recoverLock(lock);
        const descriptor = fs.openSync(lock, 'wx', 0o600);
        fs.writeFileSync(descriptor, JSON.stringify({ pid: process.pid, process_start: processStart(process.pid), authority: this.authority }));
        fs.fsyncSync(descriptor);
        try {
            return await action(this.read(reference));
        } finally {
            fs.closeSync(descriptor);
            fs.unlinkSync(lock);
        }
    }

    recoverLock(file) {
        if (!fs.existsSync(file)) return;
        if (fs.lstatSync(file).isSymbolicLink() || fs.statSync(file).size > 1024) throw new Error('workflow_lock_invalid');
        const owner = JSON.parse(fs.readFileSync(file, 'utf8'));
        if (owner.authority !== this.authority || !Number.isInteger(owner.pid) || owner.pid < 1) throw new Error('workflow_lock_invalid');
        const current = processStart(owner.pid);
        if (current === null || current !== undefined && current !== owner.process_start) fs.unlinkSync(file);
        else throw new Error('workflow_job_locked');
    }
}

function processStart(pid) {
    if (process.platform !== 'linux') return undefined;
    try {
        const text = fs.readFileSync('/proc/' + pid + '/stat', 'utf8');
        return text.slice(text.lastIndexOf(')') + 2).split(' ')[19];
    } catch (error) {
        if (error.code === 'ENOENT' || error.code === 'ESRCH') return null;
        return undefined;
    }
}
