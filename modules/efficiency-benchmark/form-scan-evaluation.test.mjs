import test from 'node:test';
import assert from 'node:assert/strict';
import { expectedFormInventory, formInventoryMatches, validatePairedCapture } from './form-scan-evaluation.mjs';

const compact = { elements: [{ name: 'Name', role: 'Edit', value: 'Alice', offscreen: true },
    { name: 'Send', role: 'button', disabled: true }, { name: 'Details', role: 'toggle button' }] };

test('inventory oracle preserves values/flags, rejects inferred rules and extra/missing controls, and ignores record order only', () => {
    const expected = expectedFormInventory(compact);
    assert.equal(expected.buttons.length, 1);
    assert.equal(formInventoryMatches(expected, expected), true);
    const changed = structuredClone(expected);
    changed.fields[0].required = true;
    assert.equal(formInventoryMatches(changed, expected), false);
    changed.fields[0].required = null;
    changed.fields.push(changed.fields[0]);
    assert.equal(formInventoryMatches(changed, expected), false);
    assert.equal(formInventoryMatches({ fields: [], buttons: [] }, expected), false);
    assert.equal(formInventoryMatches({ ...expected, extra: 'unrequested' }, expected), false);
});

test('paired observations must match exactly and cannot hide clipped contents or a changing provider flag', () => {
    const scanned = { fields: [{ key: 'name', name: 'Name', role: 'Edit', enabled: true, offscreen: true },
        { key: 'send', name: 'Send', role: 'button', enabled: false, offscreen: false }],
        contents: { fields: [{ key: 'name', value: 'Alice' }] } };
    validatePairedCapture({ compact, scanned });
    scanned.fields[0].offscreen = false;
    assert.throws(() => validatePairedCapture({ compact, scanned }), /changed/);
    scanned.fields[0].offscreen = true;
    scanned.contents.fields[0].value_truncated = true;
    assert.throws(() => validatePairedCapture({ compact, scanned }), /complete/);
});
