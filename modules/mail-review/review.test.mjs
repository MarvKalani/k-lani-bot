import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

function fixture(mode) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mail-review-test-'));
    const executable = path.join(directory, 'fixture');
    const program = `#!/usr/bin/env node
const fs=require('fs');let input='';process.stdin.on('data',data=>input+=data);process.stdin.on('end',()=>{
const mails=JSON.parse(input.split('\\nMails:\\n')[1]);const out=process.argv[process.argv.indexOf('-o')+1];
const id=${mode === 'wrong-identity' ? '999' : 'mails[0].id'};
fs.writeFileSync(out,JSON.stringify({decisions:[{id,target:'KI-Vorsortiert',needsReview:false,reason:'Invoice & receipt'}]}));
console.log(JSON.stringify({type:'item.completed',item:{type:'${mode === 'action' ? 'command_execution' : 'reasoning'}'}}));
console.log(JSON.stringify({type:'turn.completed',usage:{input_tokens:100,cached_input_tokens:25,output_tokens:12,reasoning_output_tokens:3}}));});`;
    fs.writeFileSync(executable, program, { mode: 0o700 });
    const configuration = path.join(directory, 'config.json');
    fs.writeFileSync(configuration, JSON.stringify({ executable, model: 'gpt-6-luna', reasoningEffort: 'medium', timeoutMs: 5000 }));
    const input = path.join(directory, 'input.json');
    fs.writeFileSync(input, JSON.stringify([{ id: 1, senderAddress: 'billing@example.invalid', text: 'Invoice 10 EUR' }]));
    return { directory, configuration, input };
}

for (const mode of ['normal', 'action', 'wrong-identity']) {
    test(mode, () => {
        const item = fixture(mode);
        try {
            const result = spawnSync(process.execPath, ['modules/mail-review/review.mjs', item.configuration, item.input,
                path.join(item.directory, 'output')], { timeout: 10000, encoding: 'utf8' });
            if (mode === 'normal') {
                assert.equal(result.status, 0, result.stderr);
                const report = JSON.parse(fs.readFileSync(path.join(item.directory, 'output/review.json')));
                assert.deepEqual(report.usage, { input_tokens: 100, cached_input_tokens: 25, output_tokens: 12, reasoning_output_tokens: 3 });
                assert.equal(report.applied, false);
                assert.equal(report.model, 'gpt-6-luna');
            } else {
                assert.equal(result.status, 0, result.stderr);
                const report = JSON.parse(fs.readFileSync(path.join(item.directory, 'output/review.json')));
                assert.equal(report.decisions[0].stage, 'human');
                assert.equal(report.decisions[0].target, null);
                assert.equal(report.applied, false);
            }
        } finally { fs.rmSync(item.directory, { recursive: true, force: true }); }
    });
}

test('a batch covered by confirmed rules does not start Codex', () => {
    const item = fixture('normal');
    try {
        const configuration = JSON.parse(fs.readFileSync(item.configuration));
        configuration.executable = path.join(item.directory, 'must-not-exist');
        configuration.sorting = { sender_rules: [{ id: 'confirmed', sender: 'alerts@example.invalid',
            destination: 'Werbung', approved: true }] };
        fs.writeFileSync(item.configuration, JSON.stringify(configuration));
        fs.writeFileSync(item.input, JSON.stringify([{ id: 1, senderAddress: 'alerts@example.invalid', text: 'Search results' }]));
        const result = spawnSync(process.execPath, ['modules/mail-review/review.mjs', item.configuration, item.input,
            path.join(item.directory, 'output')], { timeout: 10000, encoding: 'utf8' });
        assert.equal(result.status, 0, result.stderr);
        const report = JSON.parse(fs.readFileSync(path.join(item.directory, 'output/review.json')));
        assert.equal(report.counts.rule, 1);
        assert.equal(report.usage, null);
        assert.equal(report.inputCharacters, 0);
    } finally { fs.rmSync(item.directory, { recursive: true, force: true }); }
});
