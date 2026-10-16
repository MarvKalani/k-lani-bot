import { assessCompatibility, captureCompatibility, contractHash, schemaHashes, validateCompatibility,
    assessNativeApiScope } from '../../modules/desktop-ui-mcp/compatibility.mjs';

// Synthetic metadata proves decision/hash parity only. Real node fingerprints and signed isolated acceptance are separate prerequisites.
export function compatibilityFixtures(corpus) {
    return corpus.cases.map(fixture => {
        try {
            let reply;
            if (fixture.operation === 'hash') reply = { hash: contractHash(fixture.value) };
            else if (fixture.operation === 'schemas') reply = { schemas: schemaHashes(fixture.tools) };
            else if (fixture.operation === 'validate') {
                validateCompatibility(fixture.recorded);
                reply = { valid: true };
            } else if (fixture.operation === 'capture') reply = captureCompatibility(fixture.entry, fixture.current);
            else {
                const entry = structuredClone(fixture.entry);
                entry.compatibility = fixture.recorded ?? captureCompatibility(entry, fixture.baseline);
                if (fixture.change_entry?.name) entry.name = fixture.change_entry.name;
                if (fixture.change_entry?.selector_name) entry.steps[0].selector.name = fixture.change_entry.selector_name;
                reply = assessCompatibility(entry, fixture.current);
            }
            return { name: fixture.name, reply };
        } catch (error) {
            return { name: fixture.name, reply: { error: error.message } };
        }
    });
}

export function nativeApiFixtures(corpus) {
    return corpus.cases.map(fixture => ({ name: fixture.name, reply: assessNativeApiScope(fixture.contract, fixture.current) }));
}
