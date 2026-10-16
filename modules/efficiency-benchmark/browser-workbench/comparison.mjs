import fs from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { Workbench } from './workbench.mjs';

const protocol = JSON.parse(readFileSync(new URL('./protocol.json', import.meta.url), 'utf8'));
const template = protocol.htmlTemplate;
const requiredOperations = ['calculator.sum', 'editor.write', 'folder.create', 'file.save', 'file.read'];

function expectedCents(values) {
    let total = 0n;
    for (const value of values) {
        const [euros, cents] = value.split('.');
        total += BigInt(euros) * 100n + BigInt(cents);
    }
    return Number(total);
}

function fingerprint(text) {
    return createHash('sha256').update(text, 'utf8').digest('hex');
}

// The comparison controller supplies equal tasks and independently reads both isolated output directories.
// Verification is outside either model's task execution; checking results never operates a program on its behalf.
export class Comparison {
    constructor(root, datasetIndex = 0) {
        if (!Number.isInteger(datasetIndex) || !protocol.datasets[datasetIndex]) throw new Error('Unknown dataset.');
        this.dataset = structuredClone(protocol.datasets[datasetIndex]);
        this.sessions = {
            'computer-use': new Workbench(path.join(root, 'computer-use')),
            mcp: new Workbench(path.join(root, 'mcp')),
        };
        this.folder = 'Test-Bericht';
        this.file = 'rechnungen.html';
        this.cents = expectedCents(this.dataset.values);
        if (this.cents !== this.dataset.expectedCents) throw new Error('Dataset oracle disagrees with independent arithmetic.');
        const total = `${Math.floor(this.cents / 100)}.${String(this.cents % 100).padStart(2, '0')}`;
        this.expectedText = template.replace('{{amounts}}', this.dataset.values.join(', ')).replace('{{total}}', total);
    }

    session(name) {
        if (!Object.hasOwn(this.sessions, name)) throw new Error('Unknown comparison session.');
        return this.sessions[name];
    }

    task() {
        return { values: this.dataset.values, folder: this.folder, file: this.file,
            path: `Desktop/${this.folder}/${this.file}`, htmlTemplate: template,
            instruction: 'Add the supplied values in the calculator. Replace {{amounts}} and {{total}} in the HTML template, '
                + 'save it at the specified path and reopen the saved file. Use the same task and information scope for both routes.' };
    }

    async verifySession(name) {
        const workbench = this.session(name);
        const state = workbench.state();
        let text;
        try {
            text = await fs.readFile(path.join(workbench.root, 'Desktop', this.folder, this.file), 'utf8');
        } catch (error) {
            if (error.code !== 'ENOENT') throw error;
            return { status: 'pending', successful: false, completedOperations: state.events.length };
        }
        const checks = {
            calculation: state.calculation?.cents === this.cents
                && JSON.stringify(state.calculation.values) === JSON.stringify(this.dataset.values),
            fileContents: text === this.expectedText,
            reopened: state.openedFile?.path === this.task().path && state.openedFile.text === text,
            completedOperations: requiredOperations.every(operation => state.events.some(event => event.function === operation)),
            noOtherOutput: (await this.readOutputInventory(workbench)).onlyExpectedOutput,
        };
        const successful = Object.values(checks).every(Boolean);
        const incomplete = !checks.completedOperations && checks.calculation && checks.fileContents && checks.noOtherOutput;
        return { status: successful ? 'passed' : incomplete ? 'pending' : 'failed', successful, checks,
            bytes: Buffer.byteLength(text, 'utf8'), sha256: fingerprint(text),
            completedOperations: state.events.length,
            operations: state.events.map(event => event.function) };
    }

    async readOutputInventory(workbench) {
        const desktopEntries = await fs.readdir(path.join(workbench.root, 'Desktop'));
        const folderEntries = await fs.readdir(path.join(workbench.root, 'Desktop', this.folder));

        return { onlyExpectedOutput: desktopEntries.length === 1 && desktopEntries[0] === this.folder
            && folderEntries.length === 1 && folderEntries[0] === this.file };
    }

    async verify() {
        const computerUse = await this.verifySession('computer-use');
        const mcp = await this.verifySession('mcp');
        const successful = computerUse.successful && mcp.successful && computerUse.sha256 === mcp.sha256;
        return { status: successful ? 'passed' : [computerUse, mcp].some(result => result.status === 'failed') ? 'failed' : 'pending',
            equivalent: successful, task: this.task(), sessions: { 'computer-use': computerUse, mcp },
            modelUsage: { inputTokens: null, outputTokens: null, imageTokens: null },
            tokenMeasurement: 'Pending isolated model sessions; these are functional checks, not efficiency measurements.' };
    }
}
