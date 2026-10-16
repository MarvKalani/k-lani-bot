import assert from 'node:assert/strict';
import fs from 'node:fs';
import { test } from 'node:test';
import { parseNativeTree } from './projection.mjs';
import { bindWindowState, diffWindowStates } from './window-state.mjs';

const corpus = JSON.parse(fs.readFileSync(
    new URL('../../tests/CompactUiRegression/fixtures/window-diff.json', import.meta.url), 'utf8'));

function diffOptions(testCase) {
    return { windowHandle: testCase.window.handle, kind: testCase.window.kind, maxChars: testCase.options?.max_chars };
}

function valueOf(outcome) {
    assert.ok(!('error' in outcome), `unexpected error: ${outcome.error}`);
    return outcome.value;
}

function stepDiffs(testCase, trees) {
    const options = diffOptions(testCase);
    return trees.slice(1).map((tree, index) => valueOf(diffWindowStates(trees[index], tree, options)));
}

function boundMembers(testCase, firstTree) {
    const bindings = valueOf(bindWindowState(firstTree));
    return Object.fromEntries(Object.keys(testCase.expect_bindings).map(path => [path, bindings.get(path)]));
}

for (const testCase of corpus.cases) {
    test(`window diff ${testCase.name}`, () => {
        const trees = testCase.trees.map(tree => parseNativeTree(tree));
        const total = valueOf(diffWindowStates(trees[0], trees[trees.length - 1], diffOptions(testCase)));
        assert.deepStrictEqual(stepDiffs(testCase, trees), testCase.expect_steps);
        assert.deepStrictEqual(total, testCase.expect_total);
        assert.deepStrictEqual(boundMembers(testCase, trees[0]), testCase.expect_bindings);
    });
}
