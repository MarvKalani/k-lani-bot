export const CompatibilityFixture = Object.freeze({
    Unchanged: 'unchanged', CompatiblePatch: 'compatible-patch', MissingWriter: 'incompatible-action'
});
const inputTools = new Set(['type_text', 'click_element', 'hotkey', 'set_clipboard', 'focus_window']);

// Isolated compatibility tests label two metadata builds and remove one writer contract while forwarding actual application reads/results.
export class CompatibilityFixtureClient {
    constructor(client, profile) {
        if (!Object.values(CompatibilityFixture).includes(profile)) throw new Error('Unknown synthetic compatibility fixture.');
        this.client = client;
        this.profile = profile;
    }

    get protocolVersion() {
        return this.client.protocolVersion;
    }

    async connect() {
        const actual = await this.client.connect();
        return { ...actual, version: this.label(), compatibility_fixture: this.provenance(actual.version) };
    }

    async request(method, args) {
        const response = await this.client.request(method, args);
        if (method !== 'tools/list' || this.profile !== CompatibilityFixture.MissingWriter) return response;
        return { ...response, result: { ...response.result,
            tools: response.result.tools.filter(tool => tool.name !== 'type_text') } };
    }

    async call(name, args = {}) {
        if (this.profile === CompatibilityFixture.MissingWriter && inputTools.has(name)) {
            return { result: { structuredContent: { success: false, code: 'unsupported', outcome: 'not_dispatched',
                error: 'Synthetic incompatible action fixture refuses input before forwarding it.' } } };
        }
        const response = await this.client.call(name, args);
        if (name !== 'get_machine_capabilities') return response;
        const payload = response.result?.structuredContent
            ?? JSON.parse(response.result?.content?.find(part => part.type === 'text')?.text ?? '{}');
        if (!payload.success || !payload.capabilities) return response;
        const capabilities = { ...payload.capabilities, version: this.label(),
            compatibility_fixture: this.provenance(payload.capabilities.version) };
        if (this.profile === CompatibilityFixture.MissingWriter) {
            capabilities.activeTools = capabilities.activeTools.filter(tool => tool !== 'type_text');
        }
        const actual = { ...payload, capabilities };
        return { ...response, result: { ...response.result, structuredContent: actual,
            content: [{ type: 'text', text: JSON.stringify(actual) }] } };
    }

    label() {
        return this.profile === CompatibilityFixture.Unchanged ? 'synthetic-compatibility-build-a' : 'synthetic-compatibility-build-b';
    }

    provenance(actualVersion) {
        return { kind: 'synthetic_metadata_build', profile: this.profile, actual_node_version: actualVersion,
            application_results: 'forwarded_unchanged', authentic_computer_use: false };
    }

    close() {
        this.client.close();
    }
}
