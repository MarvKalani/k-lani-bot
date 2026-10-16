import test from 'node:test';
import assert from 'node:assert/strict';
import { planMailSorting } from './sorting.mjs';

const rules = [{ id: 'offers', domain: 'offers.example', include_subdomains: true, destination: 'Werbung', approved: true }];
function mail(id, senderAddress = 'alerts@offers.example', text = 'Your saved search results') {
    return { id, senderAddress, subject: 'Search alerts', text };
}
function review(id, needsReview = false) {
    return { id, target: 'KI-Vorsortiert', needsReview, reason: 'Requires reading.' };
}

test('confirmed rules skip both models, using SMTP domains rather than display names', async () => {
    const result = await planMailSorting([mail(1), mail(2, 'alerts@sub.offers.example')], { sender_rules: rules }, {
        decisionModel: () => assert.fail('Rule mail reached the decision model.'),
        ai: () => assert.fail('Rule mail reached AI.')
    });
    assert.deepEqual(result.decisions.map(value => value.target), ['Werbung', 'Werbung']);
    assert.equal(result.counts.rule, 2);
    assert.equal(result.applied, false);
});

test('lookalike domains and forged display names do not match a rule', async () => {
    const result = await planMailSorting([mail(1, 'alerts@eviloffers.example'),
        { ...mail(2, undefined), senderAddress: '', sender: 'alerts@offers.example' }], { sender_rules: rules }, {
        ai: async mails => mails.map(value => review(value.id))
    });
    assert.equal(result.counts.rule, 0);
    assert.equal(result.counts.ai, 2);
});

test('financial and personal exceptions reach content review', async () => {
    const result = await planMailSorting([mail(1, 'alerts@offers.example', 'Invoice with payment due'),
        mail(2, 'alerts@offers.example', 'An InMail requires reading')], { sender_rules: rules }, {
        ai: async mails => mails.map(value => review(value.id))
    });
    assert.equal(result.counts.aiMails, 2);
    assert.equal(result.counts.rule, 0);
});

test('high uncalibrated model confidence still escalates to AI', async () => {
    const result = await planMailSorting([mail(1, 'person@example.invalid')], {}, {
        decisionModel: async () => ({ outcome: 'advisory', calibration_validated: false,
            abstain_from_automatic_verdict: true, answers: { mail_category: { choice: 'advertisement', confidence: 0.999 } } }),
        ai: async () => [review(1)]
    });
    assert.equal(result.decisions[0].stage, 'ai');
    assert.equal(result.counts.decisionModelCalls, 1);
});

test('a calibrated model verdict skips AI', async () => {
    const result = await planMailSorting([mail(1, 'person@example.invalid')], {}, {
        decisionModel: async () => ({ outcome: 'advisory', calibration_validated: true,
            abstain_from_automatic_verdict: false, answers: { mail_category: { choice: 'invoice_or_payment' } } }),
        ai: () => assert.fail('A calibrated verdict reached AI.')
    });
    assert.equal(result.decisions[0].stage, 'decisionModel');
    assert.equal(result.decisions[0].target, 'Rechnungen');
});

test('a calibrated advertising verdict still reviews explicit financial evidence', async () => {
    const result = await planMailSorting([mail(1, 'billing@example.invalid', 'Invoice due tomorrow')], {}, {
        decisionModel: async () => ({ outcome: 'advisory', calibration_validated: true,
            abstain_from_automatic_verdict: false, answers: { mail_category: { choice: 'advertisement' } } }),
        ai: async () => [{ id: 1, target: 'Rechnungen', needsReview: false, reason: 'Invoice.' }]
    });
    assert.equal(result.decisions[0].stage, 'ai');
    assert.equal(result.decisions[0].target, 'Rechnungen');
});

test('model failure and AI uncertainty leave the mail for the owner', async () => {
    const result = await planMailSorting([mail(1, 'person@example.invalid')], {}, {
        decisionModel: () => { throw new Error('Unavailable'); }, ai: async () => [review(1, true)]
    });
    assert.equal(result.decisions[0].stage, 'human');
    assert.equal(result.decisions[0].target, null);
    assert.equal(result.decisions[0].needsReview, true);
});

test('conflicting or unapproved rules cannot silently route mail', async () => {
    const configuration = { sender_rules: [...rules, { ...rules[0], id: 'important', destination: 'KI-Vorsortiert' }] };
    const conflict = await planMailSorting([mail(1)], configuration, { ai: async () => [review(1, true)] });
    assert.equal(conflict.decisions[0].stage, 'human');
    const unapproved = await planMailSorting([mail(1)], { sender_rules: [{ ...rules[0], approved: false }] }, {});
    assert.equal(unapproved.decisions[0].stage, 'human');
});

test('incomplete messages and changed reviewer identities are not actionable', async () => {
    const incomplete = await planMailSorting([{ ...mail(1), truncated: true }], { sender_rules: rules }, {
        ai: () => assert.fail('An incomplete message reached AI.')
    });
    assert.equal(incomplete.decisions[0].target, null);
    const changed = await planMailSorting([mail(1, 'person@example.invalid')], {}, { ai: async () => [review(99)] });
    assert.equal(changed.decisions[0].target, null);
});
