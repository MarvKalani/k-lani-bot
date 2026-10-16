import { referenceCost } from './api-reference-cost.mjs';

function median(values) {
    const sorted = values.filter(Number.isFinite).sort((first, second) => first - second);
    if (sorted.length === 0) return null;
    const middle = Math.floor(sorted.length / 2);
    return sorted.length % 2 === 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
}

export function summarizeScreenshotStudy(report) {
    const variants = [...new Set(report.trials.map(trial => trial.route))];
    return { format: 'k-lani-bot-screenshot-study', version: 1, model: report.model, reasoningEffort: report.reasoningEffort,
        groups: variants.map(variant => group(report.trials.filter(trial => trial.route === variant), variant)),
        limitations: ['Three datasets per variant form an exploratory pilot, not a causal proof or statistical ranking.',
            'Cursor positions were not reset in this pilot; the pointer observation is exploratory.',
            'Image dimensions and PNG properties are observed at the tool boundary; provider preprocessing is not visible.',
            'Unknown usage, including timed-out attempts, remains unknown rather than zero.'],
        trials: report.trials.map(trial => ({ variant: trial.route, dataset: trial.dataset, success: trial.success,
            usage: trial.usage, elapsedMs: trial.elapsedMs, oracle: trial.oracle, measured: Boolean(trial.usage),
            imageCount: trial.metrics?.calls.reduce((sum, call) => sum + call.imageCount, 0),
            imageBytes: trial.metrics?.calls.reduce((sum, call) => sum + call.imageBytes, 0),
            toolCalls: trial.metrics?.calls.length, rejectedCalls: trial.metrics?.calls.filter(call => call.failed).length })) };
}

function group(trials, variant) {
    const images = trials.flatMap(trial => trial.metrics?.calls.flatMap(call => call.imageEvidence ?? []) ?? []);
    const clicks = trials.filter(trial => trial.success).flatMap(trial => trial.metrics?.calls.filter(call => call.tool === 'mouse_click') ?? []);
    const successes = trials.filter(trial => trial.success);
    const prices = trials.map(trial => referenceCost(trial.usage));
    return { variant, successes: successes.length, attempts: trials.length, measuredAttempts: trials.filter(trial => trial.usage).length,
        medianImagesOnSuccess: median(successes.map(trial => trial.metrics.calls.reduce((sum, call) => sum + call.imageCount, 0))),
        medianElapsedMsOnSuccess: median(successes.map(trial => trial.elapsedMs)),
        medianModelDecisionMsBeforeClick: median(clicks.map(call => call.decisionMs)),
        medianBackendClickMs: median(clicks.map(call => call.elapsedMs)),
        medianImageBytes: median(images.map(image => image.deliveredBytes)),
        imageDimensions: [...new Set(images.map(image => `${image.delivered.width}x${image.delivered.height}`))],
        pngColorTypes: [...new Set(images.map(image => image.delivered.colorType))],
        maximumPaletteColors: Math.max(0, ...images.map(image => image.delivered.paletteColors ?? 0)),
        identicalFrames: images.filter(image => image.changedPixelFraction === 0).length,
        totalUsdReference: prices.every(price => price !== null) ? prices.reduce((sum, price) => sum + price, 0) : null,
        totalTokens: trials.every(trial => trial.usage)
            ? trials.reduce((sum, trial) => sum + trial.usage.input_tokens + trial.usage.output_tokens, 0) : null };
}
