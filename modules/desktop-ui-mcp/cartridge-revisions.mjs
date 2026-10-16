import fs from 'node:fs';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';

const maximumBytes = 1024 * 1024;

// A host cartridge editor atomically replaces one active file with CAS; private intent survives process interruption.
export class CartridgeRevisions {
    constructor(file, stateRoot, validate, checkpoint = () => {}) {
        this.file = path.resolve(file);
        this.root = path.resolve(stateRoot);
        if (this.root === path.dirname(this.file) || this.root.startsWith(path.dirname(this.file) + path.sep)) {
            throw new Error('Revision recovery state must remain outside the application/version directory.');
        }
        this.validate = validate;
        this.checkpoint = checkpoint;
        const key = hash(this.file);
        fs.mkdirSync(this.root, { recursive: true, mode: 0o700 });
        if (fs.lstatSync(this.root).isSymbolicLink()) throw new Error('Revision state rejects symbolic links.');
        fs.chmodSync(this.root, 0o700);
        this.journal = path.join(this.root, key + '.json');
        this.lock = path.join(this.root, key + '.lock');
    }

    read() {
        if (!fileExists(this.file)) return { document: null, revision: 0, digest: null };
        const source = readPrivate(this.file);
        const document = JSON.parse(source);
        this.validate(document);
        const revision = document.revision ?? 1;
        if (!Number.isSafeInteger(revision) || revision < 1) throw new Error('Invalid cartridge revision.');
        return { document, revision, digest: hash(source) };
    }

    replace(document, expectedRevision, expectedDigest) {
        this.acquire();
        try {
            this.recoverLocked();
            const earlier = this.read();
            if (earlier.revision !== expectedRevision || earlier.digest !== expectedDigest) throw new Error('stale_cartridge_revision');
            const next = { ...structuredClone(document), revision: expectedRevision + 1 };
            this.validate(next);
            const text = JSON.stringify(next, null, 2) + '\n';
            if (Buffer.byteLength(text) > maximumBytes) throw new Error('Cartridge exceeds its byte limit.');
            this.writeTransaction(earlier, next, text);
            return this.read();
        } finally {
            fs.unlinkSync(this.lock);
            syncDirectory(this.root);
        }
    }

    writeTransaction(earlier, next, text) {
        fs.mkdirSync(path.dirname(this.file), { recursive: true, mode: 0o700 });
        const identifier = randomUUID();
        const temporary = path.basename(this.file) + '.' + identifier + '.tmp';
        const temporaryFile = path.join(path.dirname(this.file), temporary);
        writeDurable(temporaryFile, text);
        const intent = { format: 'k-lani-bot-cartridge-replace/1', temporary,
            previous_digest: earlier.digest, next_digest: hash(text), expected_revision: earlier.revision, next_revision: next.revision };
        writeDurable(this.journal, JSON.stringify(intent));
        syncDirectory(this.root);
        this.checkpoint('before_replace', temporaryFile);
        fs.renameSync(temporaryFile, this.file);
        syncDirectory(path.dirname(this.file));
        this.checkpoint('after_replace', this.file);
        this.recoverLocked();
    }

    recover() {
        if (!fs.existsSync(this.journal)) return { status: 'clean' };
        this.acquire();
        try {
            return this.recoverLocked();
        } finally {
            fs.unlinkSync(this.lock);
            syncDirectory(this.root);
        }
    }

    recoverLocked() {
        if (!fs.existsSync(this.journal)) return { status: 'clean' };
        const intent = JSON.parse(readPrivate(this.journal));
        if (intent.format !== 'k-lani-bot-cartridge-replace/1'
            || typeof intent.temporary !== 'string'
            || !intent.temporary.startsWith(path.basename(this.file) + '.')
            || path.basename(intent.temporary) !== intent.temporary || !intent.temporary.endsWith('.tmp')) {
            throw new Error('recoverable_invalid_revision_journal');
        }
        const current = this.read();
        const temporary = path.join(path.dirname(this.file), intent.temporary);
        if (current.digest === intent.next_digest && current.revision === intent.next_revision) {
            this.clearIntent(temporary);
            return { status: 'committed', revision: current.revision };
        }
        if (current.digest !== intent.previous_digest || current.revision !== intent.expected_revision) {
            throw new Error('recoverable_external_cartridge_change');
        }
        if (!fs.existsSync(temporary) || hash(readPrivate(temporary)) !== intent.next_digest) {
            throw new Error('recoverable_corrupt_temporary');
        }
        this.validate(JSON.parse(readPrivate(temporary)));
        this.clearIntent(temporary);
        return { status: 'previous_revision_retained', revision: current.revision };
    }

    clearIntent(temporary) {
        if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
        fs.unlinkSync(this.journal);
        syncDirectory(this.root);
        syncDirectory(path.dirname(this.file));
    }

    acquire() {
        if (fs.existsSync(this.lock)) {
            const owner = JSON.parse(readPrivate(this.lock));
            const current = processStart(owner.pid);
            if (!Number.isInteger(owner.pid) || owner.pid < 1 || current === undefined
                || current !== null && typeof owner.started !== 'string'
                || current !== null && current === owner.started) throw new Error('cartridge_revision_locked');
            fs.unlinkSync(this.lock);
        }
        writeDurable(this.lock, JSON.stringify({ pid: process.pid, started: processStart(process.pid) }));
        syncDirectory(this.root);
    }
}

function hash(text) {
    return createHash('sha256').update(text).digest('hex');
}

function fileExists(file) {
    try {
        fs.lstatSync(file);
        return true;
    } catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

function readPrivate(file) {
    const metadata = fs.lstatSync(file);
    if (!metadata.isFile() || metadata.size > maximumBytes) throw new Error('Invalid or symbolic cartridge state.');
    return fs.readFileSync(file, 'utf8');
}

function writeDurable(file, text) {
    const descriptor = fs.openSync(file, 'wx', 0o600);
    try {
        fs.writeFileSync(descriptor, text);
        fs.fsyncSync(descriptor);
    } finally {
        fs.closeSync(descriptor);
    }
}

function syncDirectory(directory) {
    const descriptor = fs.openSync(directory, 'r');
    try {
        fs.fsyncSync(descriptor);
    } finally {
        fs.closeSync(descriptor);
    }
}

function processStart(pid) {
    if (process.platform !== 'linux') return undefined;
    try {
        const source = fs.readFileSync('/proc/' + pid + '/stat', 'utf8');
        return source.slice(source.lastIndexOf(')') + 2).split(' ')[19];
    } catch (error) {
        return error.code === 'ENOENT' || error.code === 'ESRCH' ? null : undefined;
    }
}
