import { test } from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import { ScreenshotTreatment, pngInformation } from './screenshot-treatment.mjs';

test('image coordinates map back to the physical display for click, move and drag', () => {
    const treatment = new ScreenshotTreatment({ scale: 0.5 });
    treatment.geometry = { sourceWidth: 1280, sourceHeight: 720, width: 640, height: 360 };
    assert.deepEqual(treatment.mapArguments('mouse_click', { x: 200, y: 123 }), { x: 400, y: 246 });
    assert.deepEqual(treatment.mapArguments('drag_and_drop', { from_x: 5, from_y: 10, to_x: 20, to_y: 30 }),
        { from_x: 10, from_y: 20, to_x: 40, to_y: 60 });
    assert.throws(() => treatment.mapArguments('mouse_click', { x: 640, y: 0 }));
    assert.throws(() => new ScreenshotTreatment({}).mapArguments('mouse_move', { x: 0, y: 0 }));
});

test('actual images are resized and indexed without confusing the original coordinate space', {
    skip: !process.env.K_LANI_BOT_IMAGE_LIBRARY,
}, async () => {
    const library = process.env.K_LANI_BOT_IMAGE_LIBRARY;
    const sharp = (await import(pathToFileURL(library).href)).default;
    const source = await sharp({ create: { width: 1280, height: 720, channels: 3, background: '#13375b' } }).png().toBuffer();
    const treatment = new ScreenshotTreatment({ library, scale: 0.5, palette: true });
    const result = await treatment.transform({ type: 'image', data: source.toString('base64'), mimeType: 'image/png' });
    const actual = pngInformation(Buffer.from(result.image.data, 'base64'));
    assert.equal(actual.width, 640);
    assert.equal(actual.height, 360);
    assert.equal(actual.colorType, 3);
    assert.ok(actual.paletteColors <= 256);
    const repeated = await treatment.transform({ type: 'image', data: source.toString('base64'), mimeType: 'image/png' });
    assert.equal(repeated.evidence.changedPixelFraction, 0);
});
