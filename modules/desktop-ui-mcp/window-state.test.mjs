import assert from 'node:assert/strict';
import fs from 'node:fs';
import { test } from 'node:test';
import { parseNativeTree } from './projection.mjs';
import { buildWindowState } from './window-state.mjs';

const corpus = JSON.parse(fs.readFileSync(
    new URL('../../tests/CompactUiRegression/fixtures/window-state.json', import.meta.url), 'utf8'));

function buildCase(testCase) {
    return buildWindowState(parseNativeTree(testCase.tree), {
        windowHandle: testCase.window.handle,
        kind: testCase.window.kind,
        maxChars: testCase.options?.max_chars,
        cursor: testCase.options?.cursor,
    });
}

function assertLinesInOrder(lines, expectedLines) {
    let position = 0;
    for (const expected of expectedLines) {
        const found = lines.indexOf(expected, position);
        assert.notEqual(found, -1, `declaration lacks line in order: ${expected}`);
        position = found + 1;
    }
}

function assertValueExpectations(value, testCase) {
    if (testCase.expect_lines) assertLinesInOrder(value.declaration.split('\n'), testCase.expect_lines);
    if (testCase.expect_line_count !== undefined) {
        assert.equal(value.declaration.split('\n').length, testCase.expect_line_count);
    }
    if (testCase.expect_state) assert.deepStrictEqual(value.state, testCase.expect_state);
    if (testCase.expect_more) assert.deepStrictEqual(value.more, testCase.expect_more);
    if (testCase.expect_unmapped) assert.deepStrictEqual(value.unmapped, testCase.expect_unmapped);
    if (testCase.expect_hidden_omitted !== undefined) {
        assert.equal(value.hidden_omitted, testCase.expect_hidden_omitted);
    }
    if (testCase.expect_shape) assert.equal(value.shape, testCase.expect_shape);
    if (testCase.expect_part) assert.equal(value.part, testCase.expect_part);
    if (testCase.expect_absent) {
        assert.equal(JSON.stringify(value).includes(testCase.expect_absent), false);
    }
}

for (const testCase of corpus.cases) {
    test(`window state ${testCase.name}`, () => {
        const outcome = buildCase(testCase);
        const actual = 'error' in outcome ? outcome : outcome.value;
        if ('expect' in testCase) assert.deepStrictEqual(actual, testCase.expect);
        if (!('error' in outcome)) assertValueExpectations(outcome.value, testCase);
    });
}

test('password dialogs with different secrets share one shape', () => {
    const shapes = corpus.cases
        .filter(testCase => testCase.name.startsWith('password-dialog'))
        .map(testCase => buildCase(testCase).value.shape);
    assert.equal(shapes.length, 2);
    assert.equal(shapes[0], shapes[1]);
});

test('a tree with only a root row has no window', () => {
    const rootOnly = parseNativeTree(
        '[0] Root "Accessibility desktop" (autoId: "", val: null, enabled: False, offscreen: False)');
    assert.deepStrictEqual(buildWindowState(rootOnly, { windowHandle: 1, kind: 'main' }), { error: 'no_window' });
});
