import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { createDecisionModel } from './decision-model.mjs';

test('local decision service cannot authorize routing with its own confidence or calibration claims', async () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mail-decision-test-'));
    let observed = null;
    const server = http.createServer(async (request, response) => {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        observed = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        response.setHeader('Content-Type', 'application/json');
        response.end(JSON.stringify({ calibration_validated: true, abstain_from_automatic_verdict: false,
            usage: { input_tokens: 35, truncated: false }, answers: { mail_category: {
                type: 'choice', choice: 'advertisement', confidence: 0.999,
                probabilities: { advertisement: 0.999, uncertain: 0.001 }
            } } }));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const credentialFile = path.join(directory, 'credential');
        fs.writeFileSync(credentialFile, 'c'.repeat(24));
        const recipeFile = path.join(directory, 'recipe.json');
        fs.writeFileSync(recipeFile, JSON.stringify({ steps: [{ args: { questions: { mail_category: {
            type: 'choice', instructions: 'Classify mail.', criteria: { advertisement: 'Offer', uncertain: 'Unclear' }
        } } } }] }));
        const configuration = { provider: 'laya', version: 1, enabled: true,
            endpoint: `http://127.0.0.1:${server.address().port}`, allowLoopbackHttp: true,
            model: 'multilingual', modelRevision: '1'.repeat(40), credentialFile, timeoutMs: 5000, maxTokens: 1024 };
        const configurationFile = path.join(directory, 'config.json');
        fs.writeFileSync(configurationFile, JSON.stringify(configuration));
        const evaluate = createDecisionModel(configurationFile, recipeFile);
        const result = await evaluate({ subject: 'Search alerts', senderAddress: 'alerts@example.invalid', text: 'Readable prose.' });
        assert.equal(result.calibration_validated, false);
        assert.equal(result.abstain_from_automatic_verdict, true);
        assert.equal(observed.state.body, 'Readable prose.');

        const calibrationFile = path.join(directory, 'calibration.json');
        const calibration = { version: 1, approved_by_operator: true, model_revision: configuration.modelRevision,
            rubric_sha256: result.rubric_sha256, rubric_hash_algorithm: 'node-json-sorted-v1',
            held_out_dataset_sha256: '2'.repeat(64), sample_count: 1000,
            categories: { advertisement: { sample_count: 1000, precision_lower_bound: 0.995, minimum_score: 0.99 } } };
        fs.writeFileSync(calibrationFile, JSON.stringify(calibration));
        configuration.calibrationReportFile = calibrationFile;
        fs.writeFileSync(configurationFile, JSON.stringify(configuration));
        const calibrated = await createDecisionModel(configurationFile, recipeFile)({ text: 'Readable prose.' });
        assert.equal(calibrated.calibration_validated, true);

        calibration.categories.advertisement.precision_lower_bound = 0.98;
        fs.writeFileSync(calibrationFile, JSON.stringify(calibration));
        assert.equal((await createDecisionModel(configurationFile, recipeFile)({ text: 'Readable prose.' })).calibration_validated, false);
        calibration.categories.advertisement.precision_lower_bound = 0.995;
        calibration.model_revision = '3'.repeat(40);
        fs.writeFileSync(calibrationFile, JSON.stringify(calibration));
        assert.equal((await createDecisionModel(configurationFile, recipeFile)({ text: 'Readable prose.' })).calibration_validated, false);
    } finally {
        await new Promise(resolve => server.close(resolve));
        fs.rmSync(directory, { recursive: true, force: true });
    }
});
