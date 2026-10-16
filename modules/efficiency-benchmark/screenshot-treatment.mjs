import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export function pngInformation(buffer) {
    if (buffer.length < 33 || buffer.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a') throw new Error('PNG required.');
    const information = { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20), bitDepth: buffer[24], colorType: buffer[25] };
    let offset = 8;
    while (offset + 12 <= buffer.length) {
        const length = buffer.readUInt32BE(offset);
        if (offset + length + 12 > buffer.length) throw new Error('Truncated PNG.');
        if (buffer.toString('ascii', offset + 4, offset + 8) === 'PLTE') information.paletteColors = length / 3;
        offset += length + 12;
    }
    return information;
}

// Experiments transform only the image delivered to the model. The node retains physical desktop coordinates and guards.
// Mapping is tied to the last delivered image; metadata and pixel hashes remain measurements, not semantic UI shortcuts.
export class ScreenshotTreatment {
    constructor(options) {
        this.options = { scale: 1, palette: false, cursor: false, ...options };
        this.geometry = null;
        this.previousPixels = null;
        this.previousGeometry = null;
    }

    async library() {
        if (!this.sharp) {
            const imported = await import(this.options.library ? pathToFileURL(this.options.library).href : 'sharp');
            this.sharp = imported.default;
        }
        return this.sharp;
    }

    async transform(image) {
        const source = Buffer.from(image.data, 'base64');
        const sourceInfo = pngInformation(source);
        const sharp = await this.library();
        const pixels = await sharp(source).ensureAlpha().raw().toBuffer();
        const sameGeometry = this.previousGeometry?.width === sourceInfo.width && this.previousGeometry?.height === sourceInfo.height;
        let changedPixels = null;
        if (sameGeometry) {
            changedPixels = 0;
            for (let index = 0; index < pixels.length; index += 4) {
                if (pixels[index] !== this.previousPixels[index] || pixels[index + 1] !== this.previousPixels[index + 1]
                    || pixels[index + 2] !== this.previousPixels[index + 2] || pixels[index + 3] !== this.previousPixels[index + 3]) changedPixels++;
            }
        }
        this.previousPixels = pixels;
        this.previousGeometry = sourceInfo;
        let transformed = source;
        if (this.options.scale !== 1 || this.options.palette) {
            transformed = await sharp(source).resize({ width: Math.round(sourceInfo.width * this.options.scale),
                height: Math.round(sourceInfo.height * this.options.scale), kernel: 'lanczos3' })
                .png({ palette: this.options.palette === true, colours: 256, dither: 0 }).toBuffer();
        }
        const delivered = pngInformation(transformed);
        this.geometry = { sourceWidth: sourceInfo.width, sourceHeight: sourceInfo.height, width: delivered.width, height: delivered.height };
        return { image: { ...image, data: transformed.toString('base64'), mimeType: 'image/png' },
            evidence: { source: sourceInfo, delivered, sourceBytes: source.length, deliveredBytes: transformed.length,
                sourcePixelSha256: createHash('sha256').update(pixels).digest('hex'),
                changedPixelFraction: changedPixels === null ? null : changedPixels / (sourceInfo.width * sourceInfo.height),
                cursorIncluded: this.options.cursor === true, scale: this.options.scale } };
    }

    mapArguments(name, argumentsForTool) {
        if (!['mouse_click', 'mouse_move', 'drag_and_drop'].includes(name)) return argumentsForTool;
        if (!this.geometry) throw new Error('Observe a screenshot before positioning.');
        const result = { ...argumentsForTool };
        const fields = name === 'drag_and_drop' ? ['from_x', 'from_y', 'to_x', 'to_y'] : ['x', 'y'];
        for (const field of fields) {
            if (result[field] === undefined) continue;
            const horizontal = field.endsWith('x');
            const delivered = horizontal ? this.geometry.width : this.geometry.height;
            const source = horizontal ? this.geometry.sourceWidth : this.geometry.sourceHeight;
            if (!Number.isFinite(result[field]) || result[field] < 0 || result[field] >= delivered) throw new Error('Image coordinate out of range.');
            result[field] = Math.round(result[field] * source / delivered);
        }
        return result;
    }
}
