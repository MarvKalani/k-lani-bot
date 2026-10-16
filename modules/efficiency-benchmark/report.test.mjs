import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

test('legacy screenshot runs cannot be published under the Computer Use label', context => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'k-lani-bot-report-test-'));
    context.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const report = path.join(root, 'report.json');
    const destination = path.join(root, 'public');
    fs.writeFileSync(report, JSON.stringify({ trials: [{ route: 'computer-use' }] }));
    const result = spawnSync(process.execPath, ['modules/efficiency-benchmark/render-report.mjs', report, destination],
        { encoding: 'utf8' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /not authentic Computer Use evidence/);
    assert.equal(fs.existsSync(destination), false);
});
