// Publish method counts from measured tool calls without URLs, tab identifiers, scripts or private UI content.
export function summarizeUiAccess(report) {
    return {
        format: 'k-lani-bot-ui-access-evidence',
        version: 1,
        scope: 'Observed browser benchmark calls; no claim about hidden Computer Use internals.',
        trials: report.trials.filter(trial => trial.route === 'computer-use').map(trial => {
            const calls = trial.metrics?.calls ?? [];
            const scripts = calls.filter(call => !call.failed && typeof call.arguments?.code === 'string')
                .map(call => call.arguments.code);
            return {
                repetition: trial.repetition,
                successfulTask: trial.success,
                transmittedImages: calls.reduce((sum, call) => sum + call.imageCount, 0),
                successfulScriptOccurrences: {
                    getAXState: occurrences(scripts, /\.getAXState\s*\(/g),
                    setValueByElement: occurrences(scripts, /\.setValue\s*\(\s*\d+\s*,/g),
                    clickByElement: occurrences(scripts, /\.click\s*\(\s*\d+\s*\)/g),
                    clickByCoordinates: occurrences(scripts, /\.click\s*\(\s*\[/g)
                }
            };
        })
    };
}

function occurrences(scripts, expression) {
    return scripts.reduce((sum, script) => sum + [...script.matchAll(expression)].length, 0);
}
