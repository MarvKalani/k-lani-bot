import fs from 'node:fs';
import net from 'node:net';
import crypto from 'node:crypto';

const minimumValidatedPrecision = 0.99;
const maximumResponseBytes = 1024 * 1024;
const maximumRequestBytes = 128 * 1024;

// The reviewer asks the existing local decision service for advisory data only. Calibration belongs to the
// operator's held-out report, never to a confidence value or a claim supplied by the service itself.
export function createDecisionModel(configurationFile, recipeFile) {
    const configuration = JSON.parse(fs.readFileSync(configurationFile, 'utf8'));
    const endpoint = new URL('/v1/systemone', configuration.endpoint);
    const questions = JSON.parse(fs.readFileSync(recipeFile, 'utf8')).steps[0].args.questions;
    if (configuration.provider !== 'laya' || configuration.enabled !== true || configuration.version !== 1
        || endpoint.protocol !== 'http:' || configuration.allowLoopbackHttp !== true
        || !net.isIP(endpoint.hostname) || !/^127\./.test(endpoint.hostname)
        || endpoint.username || endpoint.password || !/^[a-f0-9]{40}$/.test(configuration.modelRevision)
        || !Number.isInteger(configuration.timeoutMs) || configuration.timeoutMs < 100 || configuration.timeoutMs > 60000
        || !Number.isInteger(configuration.maxTokens) || configuration.maxTokens < 64 || configuration.maxTokens > 8192) {
        throw new Error('The mail reviewer requires an enabled, pinned local loopback decision service.');
    }
    if (questions.mail_category?.type !== 'choice') throw new Error('A typed mail category rubric is required.');
    const rubricHash = crypto.createHash('sha256').update(JSON.stringify(sortProperties(questions))).digest('hex');
    return async mail => {
        const credential = fs.readFileSync(configuration.credentialFile, 'utf8').trim();
        if (credential.length < 16 || credential.length > 256 || /\s/.test(credential)) throw new Error('Invalid decision credential.');
        const body = JSON.stringify({ model: configuration.model, max_len: configuration.maxTokens, questions,
            state: { subject: mail.subject || '', sender: mail.senderAddress || mail.sender || '', body: mail.text } });
        if (Buffer.byteLength(body) > maximumRequestBytes) throw new Error('Decision input exceeds its bound.');
        const signal = AbortSignal.timeout(configuration.timeoutMs);
        const response = await fetch(endpoint, { method: 'POST', redirect: 'error', signal,
            headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${credential}` }, body });
        if (!response.ok) throw new Error('Decision service rejected the request.');
        const result = await boundedJson(response);
        validateAnswer(result, questions);
        const calibrated = calibrationCovers(configuration, rubricHash, result.answers.mail_category);
        const truncated = ![undefined, false, 0].includes(result.usage?.truncated);
        return { outcome: truncated ? 'uncertain' : 'advisory', answers: result.answers, usage: result.usage,
            provider: 'laya', model_revision: configuration.modelRevision, rubric_sha256: rubricHash,
            calibration_validated: calibrated, abstain_from_automatic_verdict: truncated || !calibrated };
    };
}

function sortProperties(value) {
    if (Array.isArray(value)) return value.map(sortProperties);
    if (value === null || typeof value !== 'object') return value;
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, sortProperties(value[key])]));
}

async function boundedJson(response) {
    let length = 0;
    const chunks = [];
    for await (const chunk of response.body) {
        length += chunk.length;
        if (length > maximumResponseBytes) throw new Error('Decision response exceeds its bound.');
        chunks.push(chunk);
    }
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function validateAnswer(result, questions) {
    const answer = result.answers?.mail_category;
    const labels = Object.keys(questions.mail_category.criteria);
    const probabilities = answer?.probabilities;
    if (!answer || answer.type !== 'choice' || !labels.includes(answer.choice)
        || !Number.isFinite(answer.confidence) || answer.confidence < 0 || answer.confidence > 1
        || !probabilities || Object.keys(result.answers).length !== 1 || Object.keys(probabilities).length !== labels.length
        || labels.some(label => !Number.isFinite(probabilities[label]) || probabilities[label] < 0 || probabilities[label] > 1)
        || Math.abs(Object.values(probabilities).reduce((sum, probability) => sum + probability, 0) - 1) > 0.000501) {
        throw new Error('Invalid decision answer distribution.');
    }
}

function calibrationCovers(configuration, rubricHash, answer) {
    if (!configuration.calibrationReportFile) return false;
    const report = JSON.parse(fs.readFileSync(configuration.calibrationReportFile, 'utf8'));
    const quality = report.categories?.[answer.choice];
    return report.version === 1 && report.approved_by_operator === true
        && report.model_revision === configuration.modelRevision && report.rubric_sha256 === rubricHash
        && report.rubric_hash_algorithm === 'node-json-sorted-v1'
        && /^[a-f0-9]{64}$/.test(report.held_out_dataset_sha256 || '')
        && Number.isSafeInteger(report.sample_count) && report.sample_count > 0
        && Number.isSafeInteger(quality?.sample_count) && quality.sample_count > 0
        && Number.isFinite(quality.precision_lower_bound) && quality.precision_lower_bound >= minimumValidatedPrecision
        && quality.precision_lower_bound <= 1 && Number.isFinite(quality.minimum_score)
        && quality.minimum_score >= 0 && quality.minimum_score <= answer.confidence;
}
