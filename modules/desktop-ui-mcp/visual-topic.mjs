import { pathToFileURL } from 'node:url';
import { pngInformation } from '../efficiency-benchmark/screenshot-treatment.mjs';

// Screenshot experiments mark observed native elements. Selection still invokes a fresh semantic identity, not a stored point.
// Crops use the same source pixels and map; they never change the native target geometry.
export class VisualTopic {
    constructor(gateway, configuration) {
        this.gateway = gateway;
        this.configuration = configuration;
        this.current = null;
        this.nextView = 1;
    }

    async call(name, args) {
        if (name === 'screen_info') return this.gateway.call('ui_info', args);
        if (name === 'screen_observe') return this.observe(args);
        if (name !== 'screen_act' || !this.current || args.view !== this.current.view || args.pid !== this.current.pid) {
            throw new Error('Use the current screenshot view and its application PID.');
        }
        const steps = this.normalizeSteps(args.steps);
        if (steps.some(step => step.action !== 'key' && !this.current.refs.has(step.ref))) {
            throw new Error('Select only an element marked in the current view.');
        }
        this.current = null;
        const result = await this.gateway.call('ui_act', { pid: args.pid, steps });
        return { success: result.success, completed_steps: result.completed_steps, error: result.error,
            failed_step: result.failed_step, retry: result.retry, mcp: result.mcp };
    }

    normalizeSteps(steps) {
        if (!Array.isArray(steps)) throw new Error('Supply action steps.');
        let focusedEdit = null;
        return steps.map(step => {
            const result = { ...step };
            if (step.action === 'write' && !step.ref) {
                if (!focusedEdit) throw new Error('write requires ref, or a preceding click on an Edit control in the same batch.');
                result.ref = focusedEdit;
            }
            if (step.action === 'click') {
                const identity = this.gateway.projection?.references.get(step.ref);
                focusedEdit = identity?.role === 'Edit' ? step.ref : null;
            }
            if (step.action === 'key') focusedEdit = null;
            return result;
        });
    }

    async observe(args) {
        const allowed = ['pid', 'region'];
        if (Object.keys(args).some(key => !allowed.includes(key))) throw new Error('Unsupported screenshot argument.');
        await this.gateway.call('ui_observe', { pid: args.pid, full: true, limit: 200 });
        const rectangles = await this.elementRectangles(args.pid);
        const readable = this.configuration.autoCrop ? await this.readableRectangles(args.pid) : [];
        const result = await this.gateway.call('ui_observe', { pid: args.pid, image: true });
        if (result.images.length !== 1) throw new Error('Expected one native screenshot.');
        const buffer = Buffer.from(result.images[0].data, 'base64');
        const geometry = pngInformation(buffer);
        const automatic = this.configuration.autoCrop && this.nextView > 1 && !args.region;
        const crop = args.region ? gridCrop(args.region, geometry) : automatic ? readableCrop(readable, geometry)
            : { left: 0, top: 0, width: geometry.width, height: geometry.height };
        const visible = rectangles.filter(rectangle => rectangle.x < crop.left + crop.width && rectangle.y < crop.top + crop.height
            && rectangle.x + rectangle.width > crop.left && rectangle.y + rectangle.height > crop.top);
        const imported = await import(pathToFileURL(this.configuration.imageLibrary).href);
        const image = await imported.default(buffer).extract(crop).composite([{ input: Buffer.from(overlay(visible, crop)) }]).png().toBuffer();
        const view = 'v' + this.nextView++;
        this.current = { view, pid: args.pid, refs: new Set(visible.map(rectangle => rectangle.ref)) };
        return { success: true, view, width: crop.width, height: crop.height, marked_elements: visible.length,
            source: 'native-accessibility-backed marks; no pixel detector',
            region: args.region ?? (automatic ? 'readable-application-region' : 'full'), source_pixels: geometry.width * geometry.height,
            delivered_pixels: crop.width * crop.height, images: [{ type: 'image', mimeType: 'image/png', data: image.toString('base64') }] };
    }

    async elementRectangles(pid) {
        const rectangles = [];
        for (const role of ['Edit', 'button', 'page tab', 'toggle button']) {
            const result = this.gateway.requireSuccess(await this.gateway.native('find_elements', {
                pid, role, visible: true, enabled: true, limit: 200
            }));
            if (result.truncated) throw new Error('Candidate list is incomplete.');
            for (const node of result.matches) {
                const binding = [...this.gateway.projection.references.entries()].filter(([, target]) => target.pid === pid
                    && target.role === node.Role && target.name === node.Name && target.autoId === node.AutoId);
                if (binding.length !== 1 || node.Bounds.width <= 0 || node.Bounds.height <= 0) continue;
                rectangles.push({ ref: binding[0][0], ...node.Bounds });
            }
        }
        return rectangles;
    }

    async readableRectangles(pid) {
        const result = this.gateway.requireSuccess(await this.gateway.native('find_elements', { pid, visible: true, limit: 200 }));
        if (result.truncated) throw new Error('Readable region is incomplete.');
        return result.matches.filter(node => /^(Edit|static|heading|button|page tab|toggle button)$/.test(node.Role)
            && [...this.gateway.projection.references.values()].some(target => target.pid === pid
                && target.role === node.Role && target.name === node.Name)).map(node => node.Bounds);
    }
}

export function readableCrop(rectangles, geometry) {
    const valid = rectangles.filter(rectangle => rectangle.width > 0 && rectangle.height > 0);
    if (!valid.length) return { left: 0, top: 0, width: geometry.width, height: geometry.height };
    const margin = 24;
    const left = Math.max(0, Math.floor(Math.min(...valid.map(rectangle => rectangle.x)) - margin));
    const top = Math.max(0, Math.floor(Math.min(...valid.map(rectangle => rectangle.y)) - margin));
    const right = Math.min(geometry.width, Math.ceil(Math.max(...valid.map(rectangle => rectangle.x + rectangle.width)) + margin));
    const bottom = Math.min(geometry.height, Math.ceil(Math.max(...valid.map(rectangle => rectangle.y + rectangle.height)) + margin));
    if (right <= left || bottom <= top) throw new Error('Readable crop is outside the observed screen.');
    return { left, top, width: right - left, height: bottom - top };
}

export function gridCrop(region, geometry) {
    if (!/^[A-H][1-8]$/.test(region)) throw new Error('Use an overview grid cell A1–H8.');
    const column = region.charCodeAt(0) - 65;
    const row = Number(region[1]) - 1;
    const firstColumn = Math.max(0, Math.min(5, column - 1));
    const firstRow = Math.max(0, Math.min(5, row - 1));
    const left = Math.floor(firstColumn * geometry.width / 8);
    const top = Math.floor(firstRow * geometry.height / 8);
    return { left, top, width: Math.floor((firstColumn + 3) * geometry.width / 8) - left,
        height: Math.floor((firstRow + 3) * geometry.height / 8) - top };
}

function overlay(rectangles, crop) {
    const markers = rectangles.map(rectangle => {
        const x = Math.max(0, rectangle.x - crop.left);
        const y = Math.max(0, rectangle.y - crop.top);
        const width = Math.min(rectangle.x + rectangle.width - crop.left, crop.width) - x;
        const height = Math.min(rectangle.y + rectangle.height - crop.top, crop.height) - y;
        const labelWidth = 8 * rectangle.ref.length + 8;
        const labelX = Math.max(0, Math.min(crop.width - labelWidth, x));
        const labelY = Math.max(0, y - 18);
        return `<rect x="${x}" y="${y}" width="${width}" height="${height}" fill="none" stroke="#ffcf4d" stroke-width="2"/>`
            + `<rect x="${labelX}" y="${labelY}" width="${labelWidth}" height="17" fill="#10213c"/>`
            + `<text x="${labelX + 4}" y="${labelY + 13}" font-family="sans-serif" font-size="12" fill="#ffffff">${rectangle.ref}</text>`;
    }).join('');
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${crop.width}" height="${crop.height}">${markers}</svg>`;
}

export const visualTools = [
    { name: 'screen_info', description: 'Read node Info and select the private application PID.',
        inputSchema: { type: 'object', properties: {}, additionalProperties: false } },
    { name: 'screen_observe', description: 'Get a screenshot with short IDs marking native editable and clickable elements. '
        + 'No tree is returned. region optionally selects a native-resolution 3x3-cell crop around A1–H8 in the full 8x8 overview. '
        + 'Use the full image when orientation or full text is missing. Intersecting targets are marked; a crop may clip their text.',
        inputSchema: { type: 'object', properties: { pid: { type: 'integer', minimum: 1 }, region: { type: 'string' } }, required: ['pid'], additionalProperties: false } },
    { name: 'screen_act', description: 'Act on IDs in the latest view: click or write literal text, or key for documented navigation. '
        + 'write uses {action:"write",ref:"e16",text:"..."}; ref may be omitted only after clicking an Edit control in the same batch. '
        + 'Uses fresh native semantics. View expires after input; observe after changed layout. First use returns generated MCP material '
        + 'when an output can be verified, otherwise a candidate. No physical coordinate clicks.',
        inputSchema: { type: 'object', properties: { pid: { type: 'integer', minimum: 1 }, view: { type: 'string' },
            steps: { type: 'array', minItems: 1, maxItems: 12, items: { type: 'object', properties: {
                action: { type: 'string', enum: ['click', 'write', 'key'] }, ref: { type: 'string' }, text: { type: 'string' },
                keys: { type: 'array', items: { type: 'string' } }
            }, required: ['action'], additionalProperties: false } } }, required: ['pid', 'view', 'steps'], additionalProperties: false } }
];
