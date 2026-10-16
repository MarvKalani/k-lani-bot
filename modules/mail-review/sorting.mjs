// The mail reviewer plans destinations without access to Outlook. Confirmed rules run before either model;
// incomplete mail, conflicting rules and unresolved model answers remain in the inbox for the owner.
export const destinations = ['KI-Vorsortiert', 'Rechnungen', 'Werbung', 'Unwichtig'];
const protectedContent = /\b(rechnung|rechnungen|invoice|billing|zahlung|payment|mahnung|steuer|beleg|receipt|password|passwort|security|sicherheit|verification|verifizierung|inmail)\b/i;

function validateRules(rules) {
    if (!Array.isArray(rules) || rules.length > 1000) throw new Error('Provide a bounded rule list.');
    const identifiers = new Set();
    for (const rule of rules) {
        if (typeof rule.id !== 'string' || !rule.id || identifiers.has(rule.id) || !destinations.includes(rule.destination)) {
            throw new Error('Rules require unique identifiers and known destinations.');
        }
        identifiers.add(rule.id);
        if (Boolean(rule.sender) === Boolean(rule.domain)) throw new Error('A rule requires one SMTP address or domain.');
        if (rule.sender && !validAddress(rule.sender)) throw new Error('A sender rule requires an exact SMTP address.');
        if (rule.domain && !/^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.[a-z]{2,}$/i.test(rule.domain)) {
            throw new Error('A domain rule requires a complete domain name.');
        }
        if (rule.exceptTerms !== undefined && (!Array.isArray(rule.exceptTerms)
            || rule.exceptTerms.some(term => typeof term !== 'string' || !term || term.length > 100))) {
            throw new Error('Rule exceptions must be bounded literal terms.');
        }
    }
}

function validAddress(address) {
    return typeof address === 'string' && /^[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+$/.test(address);
}

function matchesSender(rule, address) {
    if (!validAddress(address)) return false;
    const normalized = address.toLowerCase();
    if (rule.sender) return normalized === rule.sender.toLowerCase();
    const domain = normalized.slice(normalized.lastIndexOf('@') + 1);
    const expected = rule.domain.toLowerCase();
    return domain === expected || rule.include_subdomains === true && domain.endsWith('.' + expected);
}

function matchRules(mail, rules) {
    const content = `${mail.subject || ''}\n${mail.text}`;
    const matches = rules.filter(rule => rule.approved === true && matchesSender(rule, mail.senderAddress)
        && !(rule.exceptTerms || []).some(term => content.toLowerCase().includes(term.toLowerCase())));
    if (matches.length === 0) return { reason: 'No confirmed rule matches the observed SMTP address.' };
    if (new Set(matches.map(rule => rule.destination)).size !== 1) return { reason: 'Confirmed rules conflict.' };
    const target = matches[0].destination;
    if (['Werbung', 'Unwichtig'].includes(target) && protectedContent.test(content)) {
        return { reason: 'Possible financial document, security event or personal message requires content review.' };
    }
    return { target, ruleIds: matches.map(rule => rule.id) };
}

function decision(id, stage, target, reason, additional = {}) {
    return { id, stage, target, needsReview: target === null, reason, ...additional };
}

function proposalTarget(proposal) {
    if (proposal?.outcome !== 'advisory' || proposal.calibration_validated !== true
        || proposal.abstain_from_automatic_verdict !== false) return null;
    const targets = { invoice_or_payment: 'Rechnungen', personal_or_action: 'KI-Vorsortiert',
        advertisement: 'Werbung', automatic_no_action: 'Unwichtig' };
    return targets[proposal.answers?.mail_category?.choice] || null;
}

// Evaluators are trusted clients, not fields embedded in an email. A raw model confidence never authorizes a move.
export async function planMailSorting(mails, configuration, evaluators) {
    const rules = configuration.sender_rules || [];
    validateRules(rules);
    if (!Array.isArray(mails) || mails.length < 1 || mails.length > 100
        || mails.some(mail => !Number.isSafeInteger(mail.id)) || new Set(mails.map(mail => mail.id)).size !== mails.length) {
        throw new Error('Provide 1 to 100 mails with unique integer identities.');
    }
    const decisions = new Map();
    const pending = [];
    const modelProposals = [];
    const counts = { rule: 0, decisionModel: 0, ai: 0, human: 0, decisionModelCalls: 0, aiMails: 0 };
    for (const mail of mails) {
        if (typeof mail.text !== 'string' || mail.complete === false || mail.truncated === true) {
            decisions.set(mail.id, decision(mail.id, 'human', null, 'Complete readable mail is required.'));
            continue;
        }
        const route = matchRules(mail, rules);
        if (route.target) {
            decisions.set(mail.id, decision(mail.id, 'rule', route.target, 'Confirmed sender rule.', { ruleIds: route.ruleIds }));
            continue;
        }
        let proposal = null;
        if (evaluators.decisionModel) {
            counts.decisionModelCalls++;
            try { proposal = await evaluators.decisionModel(mail); }
            catch { proposal = { outcome: 'uncertain', error: 'Decision model unavailable.' }; }
            modelProposals.push({ id: mail.id, proposal });
        }
        let target = proposalTarget(proposal);
        if (['Werbung', 'Unwichtig'].includes(target) && protectedContent.test(`${mail.subject || ''}\n${mail.text}`)) {
            target = null;
        }
        if (target) decisions.set(mail.id, decision(mail.id, 'decisionModel', target, 'Calibrated advisory verdict.'));
        else pending.push(mail);
    }
    if (pending.length > 0) await reviewPending(pending, evaluators.ai, decisions, counts);
    const ordered = mails.map(mail => decisions.get(mail.id));
    for (const result of ordered) counts[result.stage]++;
    return { applied: false, decisions: ordered, counts, modelProposals };
}

async function reviewPending(mails, reviewer, decisions, counts) {
    counts.aiMails = mails.length;
    let results = [];
    try { results = reviewer ? await reviewer(mails) : []; }
    catch { results = []; }
    const expected = new Set(mails.map(mail => mail.id));
    const valid = Array.isArray(results) && results.length === mails.length
        && new Set(results.map(result => result.id)).size === mails.length
        && results.every(result => expected.has(result.id) && destinations.includes(result.target)
            && typeof result.needsReview === 'boolean' && typeof result.reason === 'string'
            && result.reason.length <= 180);
    for (const mail of mails) {
        const reviewed = valid ? results.find(result => result.id === mail.id) : null;
        if (reviewed && !reviewed.needsReview) decisions.set(mail.id, decision(mail.id, 'ai', reviewed.target, reviewed.reason));
        else decisions.set(mail.id, decision(mail.id, 'human', null,
            reviewed?.reason || 'AI unavailable, invalid or uncertain; leave in the inbox for the owner.'));
    }
}
