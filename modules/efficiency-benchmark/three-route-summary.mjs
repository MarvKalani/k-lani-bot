import { lunaRateCard, referenceCost, routeReferenceCosts } from './api-reference-cost.mjs';

const routeNames = ['computer-use', 'mcp-cartridge', 'bot-screenshots'];

function median(values) {
    const ordered = values.filter(Number.isFinite).sort((first, second) => first - second);
    if (ordered.length === 0) return null;
    const middle = Math.floor(ordered.length / 2);
    return ordered.length % 2 === 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
}

function sumUsage(trials, field) {
    if (trials.some(trial => !Number.isFinite(trial.usage?.[field]))) return null;
    return trials.reduce((sum, trial) => sum + trial.usage[field], 0);
}

function routeSummary(trials, route) {
    const selected = trials.filter(trial => trial.route === route);
    const successful = selected.filter(trial => trial.success);
    const totalInput = sumUsage(selected, 'input_tokens');
    const totalOutput = sumUsage(selected, 'output_tokens');
    const measured = successful.filter(trial => trial.usage);
    const completedCases = [...new Set(successful.map(trial => trial.repetition))].map(repetition => {
        const attempts = selected.filter(trial => trial.repetition === repetition);
        return { input: sumUsage(attempts, 'input_tokens'), output: sumUsage(attempts, 'output_tokens'),
            uncached: sumUsage(attempts, 'input_tokens') === null || sumUsage(attempts, 'cached_input_tokens') === null
                ? null : sumUsage(attempts, 'input_tokens') - sumUsage(attempts, 'cached_input_tokens'),
            elapsed: attempts.reduce((sum, trial) => sum + trial.elapsedMs, 0),
            calls: attempts.reduce((sum, trial) => sum + (trial.metrics?.calls.length ?? 0), 0) };
    });
    return { route, trials: selected.length, successes: successful.length,
        unmeasuredTrials: selected.filter(trial => !trial.usage).length,
        medianTaskInputTokens: median(completedCases.map(entry => entry.input)),
        medianTaskOutputTokens: median(completedCases.map(entry => entry.output)),
        medianTaskUncachedInputTokens: median(completedCases.map(entry => entry.uncached)),
        medianTaskElapsedMs: median(completedCases.map(entry => entry.elapsed)),
        medianTaskToolCalls: median(completedCases.map(entry => entry.calls)),
        medianInputTokens: median(measured.map(trial => trial.usage.input_tokens)),
        medianCachedInputTokens: median(measured.map(trial => trial.usage.cached_input_tokens)),
        medianUncachedInputTokens: median(measured.map(trial => trial.usage.input_tokens - trial.usage.cached_input_tokens)),
        medianOutputTokens: median(measured.map(trial => trial.usage.output_tokens)),
        medianElapsedMs: median(successful.map(trial => trial.elapsedMs)),
        medianToolCalls: median(successful.map(trial => trial.metrics?.calls.length ?? 0)),
        images: selected.reduce((sum, trial) => sum + (trial.metrics?.calls.reduce((count, call) => count + call.imageCount, 0) ?? 0), 0),
        detectedToolErrors: selected.reduce((sum, trial) => sum + (trial.metrics?.calls.filter(call => call.failed).length ?? 0), 0),
        totalInputTokens: totalInput, totalOutputTokens: totalOutput,
        totalCachedInputTokens: sumUsage(selected, 'cached_input_tokens'),
        totalTokensPerSuccess: successful.length > 0 && totalInput !== null && totalOutput !== null
            ? (totalInput + totalOutput) / successful.length : null };
}

function pairedSavings(trials, baseline) {
    const pairs = [];
    for (const native of trials.filter(trial => trial.route === 'mcp-cartridge' && trial.success && trial.usage)) {
        const other = trials.find(trial => trial.route === baseline && trial.repetition === native.repetition
            && trial.success && trial.usage && JSON.stringify(trial.dataset) === JSON.stringify(native.dataset));
        if (other) {
            const nativeAttempts = trials.filter(trial => trial.route === 'mcp-cartridge' && trial.repetition === native.repetition);
            const otherAttempts = trials.filter(trial => trial.route === baseline && trial.repetition === native.repetition);
            if ([...nativeAttempts, ...otherAttempts].every(trial => trial.usage)) {
                pairs.push({ nativeAttempts, otherAttempts });
            }
        }
    }
    const nativeUsage = pairs.flatMap(pair => pair.nativeAttempts);
    const baselineUsage = pairs.flatMap(pair => pair.otherAttempts);
    const nativeTokens = sumUsage(nativeUsage, 'input_tokens') + sumUsage(nativeUsage, 'output_tokens');
    const baselineTokens = sumUsage(baselineUsage, 'input_tokens') + sumUsage(baselineUsage, 'output_tokens');
    const nativeUncached = nativeTokens - sumUsage(nativeUsage, 'cached_input_tokens');
    const baselineUncached = baselineTokens - sumUsage(baselineUsage, 'cached_input_tokens');
    return { baseline, pairedSuccessfulCases: pairs.length,
        totalTokenReduction: baselineTokens > 0 ? 1 - nativeTokens / baselineTokens : null,
        uncachedInputPlusOutputReduction: baselineUncached > 0 ? 1 - nativeUncached / baselineUncached : null };
}

function publicOracle(oracle) {
    if (!oracle) return null;
    return { status: oracle.status, successful: oracle.successful, checks: oracle.checks,
        bytes: oracle.bytes, sha256: oracle.sha256, completedOperations: oracle.completedOperations, operations: oracle.operations };
}

function publicNativeUi(value) {
    if (!value) return null;
    return { calls: value.calls, failures: value.failures, resultBytes: value.resultBytes, coordinateClicks: value.coordinateClicks };
}

export function summarizeThreeRoutes(report) {
    const availableRoutes = [...routeNames];
    if (report.trials.some(trial => trial.route === 'chrome-devtools')) availableRoutes.push('chrome-devtools');
    if (report.trials.some(trial => trial.route === 'bot-hybrid')) availableRoutes.push('bot-hybrid');
    if (report.trials.some(trial => trial.route === 'bot-compact')) availableRoutes.push('bot-compact');
    return { format: 'k-lani-bot-three-route-summary', version: 1,
        fixture: report.fixture, model: report.model, reasoningEffort: report.reasoningEffort,
        modelClient: report.modelClient, createdAt: report.createdAt,
        environment: { chrome: report.environment?.chrome, botDisplay: report.environment?.botDisplay,
            computerUseDisplay: report.environment?.computerUseDisplay },
        limitations: report.limitations, groups: availableRoutes.map(route => routeSummary(report.trials, route)),
        hybridAccess: report.hybridAccess ?? null,
        compactAccess: report.compactAccess ?? null,
        apiReferencePricing: { rateCard: lunaRateCard, groups: availableRoutes.map(route => routeReferenceCosts(report.trials, route)),
            recipeAuthoringUsdReference: referenceCost(report.authoring?.usage) },
        pairedSavings: availableRoutes.filter(route => route !== 'mcp-cartridge').map(route => pairedSavings(report.trials, route)),
        authoring: { scope: 'AI recipe creation only; framework development and SDK repair tokens were not isolated.',
            usage: report.authoring?.usage ?? null, validated: report.authoring?.validation?.successful ?? false },
        trials: report.trials.map(trial => ({ route: trial.route, dataset: trial.dataset, repetition: trial.repetition,
            attempt: trial.attempt ?? 0, success: trial.success, usage: trial.usage, elapsedMs: trial.elapsedMs,
            toolCalls: trial.metrics?.calls.length ?? null,
            toolServerVersion: trial.metrics?.serverInfo?.version ?? null,
            semanticObservations: trial.metrics?.calls.filter(call => ['get_ui_tree', 'find_elements'].includes(call.tool)).length ?? null,
            semanticActionDispatches: trial.metrics?.calls.filter(call => ['click_element', 'type_text'].includes(call.tool)
                && !call.failed).length ?? null,
            semanticActionFailures: trial.metrics?.calls.filter(call => ['click_element', 'type_text'].includes(call.tool)
                && call.failed).length ?? null,
            physicalClicks: trial.metrics?.calls.filter(call => call.tool === 'mouse_click').length ?? null,
            nativeUi: publicNativeUi(trial.nativeUi),
            toolDescriptionsBytes: trial.metrics?.schemaBytes ?? null,
            functionSchemasBytes: trial.metrics?.functionSchemaBytes ?? null,
            resultBytes: trial.metrics?.calls.reduce((sum, call) => sum + call.resultJsonBytes, 0) ?? null,
            images: trial.metrics?.calls.reduce((sum, call) => sum + call.imageCount, 0) ?? null,
            imageBytes: trial.metrics?.calls.reduce((sum, call) => sum + call.imageBytes, 0) ?? null,
            imageTokens: null, toolSchemaTokens: null, retries: null, oracle: publicOracle(trial.oracle) })) };
}
