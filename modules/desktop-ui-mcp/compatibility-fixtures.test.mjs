import test from 'node:test';
import assert from 'node:assert/strict';
import { CompatibilityFixtureClient } from './compatibility-fixtures.mjs';

test('declared synthetic metadata builds preserve actual implementation fingerprints and application outputs', async () => {
    const actual = { success: true, tree: 'Actual application content' };
    const base = { protocolVersion: '2025-06-18', connect: async () => ({ version: 'real-node-version' }),
        call: async name => ({ result: { structuredContent: name === 'get_machine_capabilities'
            ? { success: true, capabilities: { version: 'real-node-version', nativeImplementationSha256: 'a'.repeat(64), activeTools: ['type_text'] } }
            : actual } }) };
    const first = new CompatibilityFixtureClient(base, 'unchanged');
    const patch = new CompatibilityFixtureClient(base, 'compatible-patch');
    assert.notEqual((await first.connect()).version, (await patch.connect()).version);
    for (const client of [first, patch]) {
        const capabilities = (await client.call('get_machine_capabilities')).result.structuredContent.capabilities;
        assert.equal(capabilities.nativeImplementationSha256, 'a'.repeat(64));
        assert.equal(capabilities.compatibility_fixture.kind, 'synthetic_metadata_build');
        assert.equal(capabilities.compatibility_fixture.actual_node_version, 'real-node-version');
        assert.deepEqual((await client.call('get_ui_tree')).result.structuredContent, actual);
    }
});

test('incompatible fixture removes the writer contract and blocks every native input before forwarding', async () => {
    const forwarded = [];
    const base = { request: async () => ({ result: { tools: [{ name: 'type_text' }, { name: 'get_ui_tree' }] } }),
        call: async name => { forwarded.push(name); return { result: { structuredContent: { success: true } } }; } };
    const client = new CompatibilityFixtureClient(base, 'incompatible-action');
    assert.deepEqual((await client.request('tools/list', {})).result.tools.map(tool => tool.name), ['get_ui_tree']);
    for (const tool of ['type_text', 'click_element', 'hotkey', 'set_clipboard', 'focus_window']) {
        assert.equal((await client.call(tool)).result.structuredContent.outcome, 'not_dispatched');
    }
    assert.deepEqual(forwarded, []);
    await client.call('get_ui_tree');
    assert.deepEqual(forwarded, ['get_ui_tree']);
});
