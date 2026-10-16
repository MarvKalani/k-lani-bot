export const lunaRateCard = {
    model: 'gpt-6-luna', verifiedOn: '2026-10-06',
    source: 'https://developers.openai.com/api/docs/models/gpt-6-luna',
    currency: 'USD', perMillionTokens: { input: 0.10, cachedInput: 0.01, cacheWrite: 0.125, output: 0.50 },
    assumptions: ['Standard API processing, short request context, no regional premium or other tool/infrastructure fees.',
        'These are reference prices, not a Codex subscription invoice.',
        'Aggregate trial usage cannot determine whether any individual request exceeded the long-context threshold.'],
};

// A reference price uses measured token categories; it never treats cached input as free or adds reasoning twice.
export function referenceCost(usage, multiplier = { input: 1, output: 1 }) {
    if (!usage) return null;
    const write = usage.cache_write_input_tokens ?? 0;
    const ordinary = usage.input_tokens - usage.cached_input_tokens - write;
    if (![ordinary, write, usage.cached_input_tokens, usage.output_tokens].every(value => Number.isFinite(value) && value >= 0)) {
        return null;
    }
    const rate = lunaRateCard.perMillionTokens;
    return (ordinary * rate.input * multiplier.input + usage.cached_input_tokens * rate.cachedInput * multiplier.input
        + write * rate.cacheWrite * multiplier.input + usage.output_tokens * rate.output * multiplier.output) / 1000000;
}

export function routeReferenceCosts(trials, route) {
    const selected = trials.filter(trial => trial.route === route);
    const successes = selected.filter(trial => trial.success).length;
    if (selected.some(trial => referenceCost(trial.usage) === null)) return null;
    const total = selected.reduce((sum, trial) => sum + referenceCost(trial.usage), 0);
    const longContextUpper = selected.reduce((sum, trial) => sum + referenceCost(trial.usage, { input: 2, output: 1.5 }), 0);
    return { route, totalUsdReference: total, usdPerSuccessReference: successes > 0 ? total / successes : null,
        longContextUpperUsdReference: longContextUpper, actualChargedUsd: null };
}
