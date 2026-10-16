import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

// Only the checked synthetic GTK binary is exercised. Its Linux window backend reports an unknown version;
// the harness pins and records the binary SHA-256 and binds every action to the freshly started fixture PID.
export function createCartridge(directory) {
    fs.mkdirSync(path.join(directory, 'macros'), { recursive: true });
    const processParameter = { name: 'pid', type: 'integer', description: 'Bound by Info to the isolated fixture.', required: true };
    const read = { version: 2, name: 'benchmark_read_setup', description: 'Read the current visible setup from the synthetic GTK fixture.',
        parameters: [processParameter], steps: [{ action: 'get_ui_tree', args: { pid: '{{pid}}', max_depth: 6, max_nodes: 80 } }],
        postconditions: [], sha256: '' };
    const change = { version: 2, name: 'benchmark_set_profile',
        description: 'Set and apply one process profile through freshly observed object identities; verify the exact resulting status.',
        parameters: [processParameter, { name: 'profile', type: 'string', description: 'Literal requested profile name.', required: true }],
        steps: [
            { action: 'find_elements', args: { pid: '{{pid}}', name: 'Process profile edit' }, expect_count: 1, capture: 'editor' },
            { action: 'type_text', args: { id: '{{editor.matches.0.Id}}', text: '{{profile}}' } },
            { action: 'find_elements', args: { pid: '{{pid}}', name: 'Edit process profile' }, expect_count: 1, capture: 'apply' },
            { action: 'silent_click', args: { id: '{{apply.matches.0.Id}}' } }
        ], postconditions: [{ pid: '{{pid}}', name: 'Fixture result', expected_state: 'exists', expected_text: 'Process edited: {{profile}}' }],
        sha256: '' };
    const recipes = [read, change];
    const files = recipes.map((recipe, index) => {
        const relative = `macros/${index}.json`;
        const bytes = Buffer.from(JSON.stringify(recipe, null, 2));
        fs.writeFileSync(path.join(directory, relative), bytes);
        return { path: relative, bytes: bytes.length, sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
    });
    const manifest = { format: 'k-lani-bot.local-macro-bundle.v1', id: 'k-lani-bot.benchmark.gtk-lab', version: '1.0.0',
        target: { application: 'Synthetic GTK cartridge lab', process_name: 'x11-client', application_version: 'unknown',
            platform: 'linux', architecture: 'x64', tested: 'linux-x64' },
        entrypoints: recipes.map((recipe, index) => ({ name: recipe.name, recipe: files[index].path,
            topic: index === 0 ? 'Use/Setup/Read' : 'Use/Setup/Edit', compatibility: 'ExactBuild', validation: 'fixture_tested',
            risk: index === 0 ? 'secure' : 'probably_harmful', scopes: index === 0 ? ['observe'] : ['observe', 'ui'] })),
        files, automatic_activation: false, release_approved: false, secured: false, dependencies: [] };
    fs.writeFileSync(path.join(directory, 'cartridge.json'), JSON.stringify(manifest, null, 2));
    return 'k-lani-bot.benchmark.gtk-lab:unknown';
}
