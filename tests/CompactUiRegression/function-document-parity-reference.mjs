import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { LearnedFunctions } from '../../modules/desktop-ui-mcp/learned-functions.mjs';

// These synthetic files exercise the actual legacy library loader; no installed application or real private state is read.
export function functionDocumentFixtures(corpus) {
    return corpus.cases.map(fixture => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-document-reference-'));
        const application = { key: 'library-fixture', version: '1.0', identity_source: 'controlled-test-fixture' };
        const target = path.join(directory, 'programs', application.key, application.version);
        fs.mkdirSync(target, { recursive: true });
        fs.writeFileSync(path.join(target, 'generated-ui.json'), JSON.stringify(fixture.document));
        try {
            const library = new LearnedFunctions({ cartridgeRoot: directory,
                revisionStateRoot: path.join(directory + '-state'), application });
            return { name: fixture.name, reply: { revision: library.generation, source_version: library.sourceVersion,
                functions: library.functions, candidates: library.candidates } };
        } catch (error) {
            return { name: fixture.name, reply: { error: error.message } };
        } finally {
            fs.rmSync(directory, { recursive: true, force: true });
            fs.rmSync(directory + '-state', { recursive: true, force: true });
        }
    });
}
