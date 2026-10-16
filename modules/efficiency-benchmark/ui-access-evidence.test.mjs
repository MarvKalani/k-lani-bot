import test from 'node:test';
import assert from 'node:assert/strict';
import { summarizeUiAccess } from './ui-access-evidence.mjs';

test('access evidence distinguishes element and coordinate calls without publishing raw scripts', () => {
    const evidence = summarizeUiAccess({ trials: [{ route: 'computer-use', repetition: 0, success: true, metrics: { calls: [
        { failed: false, imageCount: 0, arguments: { code: 'let tab = await cua.getTab("PRIVATE_TAB"); '
            + 'await tab.setValue(22, "PRIVATE_TEXT"); await tab.click(23); await tab.getAXState();' } },
        { failed: false, imageCount: 1, arguments: { code: 'await tab.click([22, 23]); await tab.getAXState();' } },
        { failed: true, imageCount: 0, arguments: { code: 'await tab.getAXState();' } }
    ] } }] });
    assert.deepEqual(evidence.trials[0].successfulScriptOccurrences,
        { getAXState: 2, setValueByElement: 1, clickByElement: 1, clickByCoordinates: 1 });
    assert.equal(evidence.trials[0].transmittedImages, 1);
    assert.equal(JSON.stringify(evidence).includes('PRIVATE_'), false);
});
