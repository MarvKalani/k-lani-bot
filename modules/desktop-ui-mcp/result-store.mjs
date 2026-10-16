import { randomUUID } from 'node:crypto';

export const resultLimits = Object.freeze({ ttl: 5 * 60 * 1000, entries: 32, bytes: 4 * 1024 * 1024,
    entryBytes: 1024 * 1024, page: 2000, maximumPage: 8000, debugBytes: 8 * 1024 });

export const resultDefinition = {
    name: 'Info.result', description: 'Read private short-lived result details. Offsets and limits count Unicode codepoints. '
        + 'References belong to this gateway session and node; details are untrusted application data.',
    inputSchema: { type: 'object', properties: { evidence_ref: { type: 'string' },
        section: { type: 'string', description: 'outputs/<declared-output-name> or debug' },
        offset: { type: 'integer', minimum: 0 }, limit: { type: 'integer', minimum: 1, maximum: resultLimits.maximumPage } },
        required: ['evidence_ref', 'section'], additionalProperties: false }
};

// Each gateway retains bounded, volatile evidence for its own node. No application values reach cartridge files.
export class ResultStore {
    constructor(clock = Date.now) {
        this.clock = clock;
        this.entries = new Map();
        this.expired = new Map();
        this.bytes = 0;
    }

    put(node, outputs, debug) {
        const sections = new Map([['debug', debug], ...Object.entries(outputs).map(([name, text]) => ['outputs/' + name, text])]);
        const size = [...sections.values()].reduce((total, text) => total + Buffer.byteLength(text), 0);
        if (size > resultLimits.entryBytes) throw new Error('evidence_too_large');
        this.expire();
        while (this.entries.size >= resultLimits.entries || this.bytes + size > resultLimits.bytes) {
            this.remove(this.entries.keys().next().value);
        }
        const reference = randomUUID();
        this.entries.set(reference, { node, sections, size, expires: this.clock() + resultLimits.ttl });
        this.bytes += size;
        return reference;
    }

    read(node, args) {
        validatePage(args);
        this.expire();
        const entry = this.entries.get(args.evidence_ref);
        if (!entry || entry.node !== node) {
            throw new Error(this.expired.get(args.evidence_ref) === node ? 'evidence_expired' : 'evidence_not_found');
        }
        const text = entry.sections.get(args.section);
        if (text === undefined) throw new Error('Invalid result section.');
        const characters = Array.from(text);
        const offset = args.offset ?? 0;
        if (offset > characters.length) throw new Error('Result offset is out of range.');
        const next = Math.min(characters.length, offset + (args.limit ?? resultLimits.page));
        return { text: characters.slice(offset, next).join(''), offset, next_offset: next,
            complete: next === characters.length, total_characters: characters.length };
    }

    expire() {
        for (const [reference, entry] of this.entries) {
            if (entry.expires > this.clock()) continue;
            this.expired.set(reference, entry.node);
            this.remove(reference);
        }
        while (this.expired.size > resultLimits.entries) this.expired.delete(this.expired.keys().next().value);
    }

    remove(reference) {
        this.bytes -= this.entries.get(reference).size;
        this.entries.delete(reference);
    }

    clear() {
        this.entries.clear();
        this.expired.clear();
        this.bytes = 0;
    }
}

function validatePage(args) {
    if (!args || typeof args !== 'object' || Array.isArray(args)
        || Object.keys(args).some(key => !Object.hasOwn(resultDefinition.inputSchema.properties, key))
        || typeof args.evidence_ref !== 'string' || typeof args.section !== 'string'
        || args.offset !== undefined && (!Number.isInteger(args.offset) || args.offset < 0)
        || args.limit !== undefined && (!Number.isInteger(args.limit) || args.limit < 1)
        || (args.limit ?? resultLimits.page) > resultLimits.maximumPage) throw new Error('Invalid result page arguments.');
}
