// Missing token breakdowns remain unknown. Failed trials stay in the totals and success rate;
// savings compare paired successful tasks while cost per success includes all measured attempts.
export function summarizeTrials(trials) {
    const groups = [];
    for (const task of [...new Set(trials.map(trial => trial.task))]) {
        for (const route of ['mcp-cartridge', 'screenshot-adapter']) {
            const selected = trials.filter(trial => trial.task === task && trial.route === route);
            const measured = selected.filter(trial => trial.usage);
            const successes = selected.filter(trial => trial.success).length;
            const totalInput = measured.reduce((sum, trial) => sum + trial.usage.input_tokens, 0);
            const totalOutput = measured.reduce((sum, trial) => sum + trial.usage.output_tokens, 0);
            groups.push({ task, route, trials: selected.length, successes,
                unmeasuredTrials: selected.length - measured.length,
                medianInputTokens: median(measured.map(trial => trial.usage.input_tokens)),
                medianUncachedInputTokens: median(measured.map(trial => trial.usage.input_tokens - trial.usage.cached_input_tokens)),
                medianOutputTokens: median(measured.map(trial => trial.usage.output_tokens)),
                medianElapsedMs: median(selected.map(trial => trial.elapsedMs)),
                totalInputTokens: totalInput, totalOutputTokens: totalOutput,
                inputTokensPerSuccess: successes > 0 && measured.length === selected.length ? totalInput / successes : null,
                images: selected.reduce((sum, trial) => sum + trial.metrics.calls.reduce((total, call) => total + call.imageCount, 0), 0),
                toolErrors: selected.reduce((sum, trial) => sum + trial.metrics.calls.filter(call => call.failed).length, 0) });
        }
    }
    return groups;
}

function median(values) {
    if (values.length === 0) return null;
    const sorted = [...values].sort((first, second) => first - second);
    const middle = Math.floor(sorted.length / 2);
    return sorted.length % 2 === 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
}
