import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { CompatibilityEvidence } from '../../modules/desktop-ui-mcp/compatibility-evidence.mjs';

// Synthetic keys and proof digests are test data only; this compares actual JS signing/reading, not a real application oracle.
export function receiptFixtures(corpus) {
    return corpus.cases.map(fixture => {
        const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-receipt-reference-'));
        fs.chmodSync(root, 0o700);
        fs.writeFileSync(path.join(root, 'receipt-signing.key'), Buffer.alloc(32, 17), { mode: 0o600 });
        try {
            const store = new CompatibilityEvidence({ evidenceRoot: root, authority: fixture.authority });
            const reference = store.record(fixture.entry, fixture.current, fixture.proof);
            const file = path.join(root, reference + '.json');
            const signed = JSON.parse(fs.readFileSync(file, 'utf8'));
            signed.payload.created_at = '2026-10-08T00:00:00.000Z';
            signed.signature = store.sign(signed.payload);
            const original = structuredClone(signed);
            if (fixture.mutation === 'signature') signed.signature = '0'.repeat(64);
            if (fixture.mutation === 'payload') signed.payload.authority = '0'.repeat(64);
            fs.writeFileSync(file, JSON.stringify(signed), { mode: 0o600 });
            const reader = fixture.foreign_authority ? new CompatibilityEvidence({ evidenceRoot: root,
                authority: { ...fixture.authority, principal: 'foreign' } }) : store;
            const compatibility = reader.read(reference, fixture.entry, fixture.observed ?? fixture.current);
            return { name: fixture.name, reply: { payload: original.payload, signature: original.signature, compatibility } };
        } catch (error) {
            return { name: fixture.name, reply: { error: error.message } };
        } finally {
            fs.rmSync(root, { recursive: true, force: true });
        }
    });
}
