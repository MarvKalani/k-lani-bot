import { createHash } from 'node:crypto';

export const CompatibilityStatus = Object.freeze({
    Reusable: 'reusable', NeedsRevalidation: 'needs_revalidation', Incompatible: 'incompatible'
});
const digestPattern = /^[a-f0-9]{64}$/;
const wireVersions = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];

export function contractHash(value) {
    return createHash('sha256').update(JSON.stringify(canonical(value))).digest('hex');
}

function canonical(value) {
    if (Array.isArray(value)) return value.map(canonical);
    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
    }
    return value;
}

const schemaMapKeywords = ['properties', '$defs', 'definitions'];
const schemaListKeywords = ['anyOf', 'oneOf', 'allOf'];

// Mirrors the C# projection: only a string description in a schema position is wording, never enum, const, default or examples.
export function schemaWithoutDescriptions(schema) {
    const copy = structuredClone(schema);
    projectSchema(copy);
    return copy;
}

function projectSchema(schema) {
    if (!isSchemaObject(schema)) return;
    if (typeof schema.description === 'string') delete schema.description;
    for (const keyword of schemaMapKeywords) {
        if (isSchemaObject(schema[keyword])) Object.values(schema[keyword]).forEach(entry => projectSchema(entry));
    }
    for (const keyword of schemaListKeywords) {
        if (Array.isArray(schema[keyword])) schema[keyword].forEach(entry => projectSchema(entry));
    }
    if (Array.isArray(schema.items)) schema.items.forEach(entry => projectSchema(entry));
    else projectSchema(schema.items);
    projectSchema(schema.additionalProperties);
}

function isSchemaObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function requiredActions(entry) {
    const actions = new Set(['get_machine_capabilities', 'list_windows', 'get_ui_tree']);
    for (const step of entry.steps) {
        if (step.action === 'read') continue;
        if (step.action === 'click' || step.action === 'select') actions.add('click_element');
        if (step.action === 'write') {
            actions.add('type_text');
            if (step.route.startsWith('semantic-focus+keyboard')) {
                for (const action of ['click_element', 'set_clipboard', 'hotkey']) actions.add(action);
            }
        }
    }
    return [...actions].sort();
}

export function schemaHashes(tools) {
    if (!Array.isArray(tools)) throw new Error('Observed native tool descriptors are required.');
    const hashes = {};
    for (const tool of tools) {
        if (typeof tool.name !== 'string' || !tool.inputSchema || Object.hasOwn(hashes, tool.name)) {
            throw new Error('Ambiguous or incomplete native tool descriptors.');
        }
        hashes[tool.name] = contractHash({
            inputSchema: schemaWithoutDescriptions(tool.inputSchema),
            outputSchema: schemaWithoutDescriptions(tool.outputSchema ?? null)
        });
    }
    return hashes;
}

export function functionScope(entry) {
    return contractHash({ name: entry.name, steps: entry.steps, outputs: entry.outputs, inputSchema: entry.inputSchema });
}

export function captureCompatibility(entry, current) {
    const actions = requiredActions(entry);
    if (!current.node_version || current.node_version === 'unknown' || current.application?.version === 'unknown'
        || !wireVersions.includes(current.wire_version) || !digestPattern.test(current.native_implementation)
        || !digestPattern.test(current.gateway_implementation) || actions.some(action => !digestPattern.test(current.action_schemas?.[action]))) {
        throw new Error('Compatibility capture requires actual wire, action-schema and implementation fingerprints.');
    }
    return { format: 'k-lani-bot-compatibility/2', node_version: current.node_version,
        node_wire_version: current.wire_version, platform: current.platform,
        application: { key: current.application.key, build: current.application.version },
        access_route: entry.steps.some(step => step.route.startsWith('semantic-focus+keyboard')) ? 'accessibility_with_keyboard' : 'accessibility',
        action_schemas: Object.fromEntries(actions.map(action => [action, current.action_schemas[action]])),
        implementations: { native: current.native_implementation, gateway: current.gateway_implementation },
        selector_scope: contractHash(entry.steps.map(step => step.selector)), function_scope: functionScope(entry) };
}

export function assessCompatibility(entry, current) {
    const recorded = entry.compatibility;
    if (!current.node_version || current.node_version === 'unknown' || !current.application?.version || current.application.version === 'unknown'
        || !current.platform || current.platform === 'unknown') return verdict(CompatibilityStatus.NeedsRevalidation, 'identity_unknown');
    if (!recorded.format) return assessLegacy(recorded, current);
    validateCompatibility(recorded);
    if (recorded.application.key !== current.application.key || recorded.application.build !== current.application.version
        || recorded.platform !== current.platform) return verdict(CompatibilityStatus.Incompatible, 'application_scope_changed');
    if (recorded.function_scope !== functionScope(entry)
        || recorded.selector_scope !== contractHash(entry.steps.map(step => step.selector))) {
        return verdict(CompatibilityStatus.Incompatible, 'function_scope_changed');
    }
    if (!current.wire_version) return verdict(CompatibilityStatus.NeedsRevalidation, 'wire_version_unknown');
    if (recorded.node_wire_version !== current.wire_version) return verdict(CompatibilityStatus.Incompatible, 'wire_version_changed');
    for (const action of requiredActions(entry)) {
        if (!current.action_schemas) return verdict(CompatibilityStatus.NeedsRevalidation, 'action_contracts_unknown');
        if (!Object.hasOwn(current.action_schemas, action)) return verdict(CompatibilityStatus.Incompatible, 'required_action_absent', action);
        if (recorded.action_schemas[action] !== current.action_schemas[action]) {
            return verdict(CompatibilityStatus.NeedsRevalidation, 'required_action_schema_changed', action);
        }
    }
    if (!digestPattern.test(current.native_implementation) || !digestPattern.test(current.gateway_implementation)) {
        return verdict(CompatibilityStatus.NeedsRevalidation, 'implementation_unknown');
    }
    if (recorded.implementations.native !== current.native_implementation || recorded.implementations.gateway !== current.gateway_implementation) {
        return verdict(CompatibilityStatus.NeedsRevalidation, 'implementation_changed');
    }
    return verdict(CompatibilityStatus.Reusable, 'verified_contract_unchanged');
}

function assessLegacy(recorded, current) {
    if (recorded.application_version !== current.application.version || recorded.platform !== current.platform) {
        return verdict(CompatibilityStatus.Incompatible, 'legacy_application_scope_changed');
    }
    if (recorded.node_version !== current.node_version) return verdict(CompatibilityStatus.NeedsRevalidation, 'legacy_contract_evidence_missing');
    return verdict(CompatibilityStatus.Reusable, 'legacy_exact_version_only');
}

function verdict(status, reason, action = null) {
    return { status, reason, ...(action ? { action } : {}) };
}

export function validateCompatibility(value) {
    const fields = ['format', 'node_version', 'node_wire_version', 'platform', 'application', 'access_route',
        'action_schemas', 'implementations', 'selector_scope', 'function_scope'];
    if (!value || Object.keys(value).some(field => !fields.includes(field) && field !== 'acceptance_ref')
        || fields.some(field => !Object.hasOwn(value, field))
        || value.format !== 'k-lani-bot-compatibility/2' || !wireVersions.includes(value.node_wire_version)
        || typeof value.node_version !== 'string' || !value.node_version || typeof value.platform !== 'string'
        || !['accessibility', 'accessibility_with_keyboard'].includes(value.access_route)) throw new Error('Unsupported compatibility contract.');
    if (!value.application || Object.keys(value.application).length !== 2 || typeof value.application.key !== 'string'
        || typeof value.application.build !== 'string' || !value.application.build || value.application.build === 'unknown') {
        throw new Error('Compatibility requires the actual application variant/build.');
    }
    if (!value.action_schemas || !Object.keys(value.action_schemas).length || Object.keys(value.action_schemas).length > 20
        || Object.values(value.action_schemas).some(digest => !digestPattern.test(digest))
        || !value.implementations || Object.keys(value.implementations).length !== 2
        || !digestPattern.test(value.implementations.native) || !digestPattern.test(value.implementations.gateway)
        || !digestPattern.test(value.function_scope) || !digestPattern.test(value.selector_scope)) {
        throw new Error('Compatibility requires bounded schema, implementation and scope fingerprints.');
    }
    if (value.acceptance_ref !== undefined && (typeof value.acceptance_ref !== 'string'
        || !/^[a-f0-9-]{36}$/.test(value.acceptance_ref))) throw new Error('Invalid acceptance reference.');
}

export function assessNativeApiScope(contract, current) {
    const tested = contract.tested_range;
    if (!current.application?.version || current.application.version === 'unknown') {
        return verdict(CompatibilityStatus.NeedsRevalidation, 'identity_unknown');
    }
    if (contract.application_key !== current.application.key || contract.platform !== current.platform) {
        return verdict(CompatibilityStatus.Incompatible, 'application_scope_changed');
    }
    if (!tested || !tested.evidence_ref || !Array.isArray(contract.required_capabilities)
        || contract.required_capabilities.length < 1 || contract.required_capabilities.length > 16) {
        return verdict(CompatibilityStatus.NeedsRevalidation, 'tested_native_api_range_missing');
    }
    const minimum = versionParts(tested.minimum);
    const maximum = versionParts(tested.maximum);
    const actual = versionParts(current.application.version);
    if (!minimum || !maximum || !actual || compareVersions(minimum, maximum) > 0) {
        return verdict(CompatibilityStatus.NeedsRevalidation, 'native_api_version_unknown');
    }
    if (compareVersions(actual, minimum) < 0 || compareVersions(actual, maximum) > 0) {
        return verdict(CompatibilityStatus.Incompatible, 'native_api_build_outside_tested_range');
    }
    for (const capability of contract.required_capabilities) {
        const probe = current.probes?.[capability];
        if (!probe || probe.source !== 'native_api_probe' || probe.function !== contract.function
            || probe.mutating !== false || probe.available !== true || !probe.evidence_ref) {
            return verdict(CompatibilityStatus.NeedsRevalidation, 'required_native_api_probe_missing', capability);
        }
    }
    return verdict(CompatibilityStatus.Reusable, 'tested_native_api_range_and_probes');
}

function versionParts(value) {
    if (typeof value !== 'string' || !/^\d{1,9}(\.\d{1,9}){0,3}$/.test(value)) return null;
    const parts = value.split('.').map(Number);
    while (parts.length < 4) parts.push(0);
    return parts;
}

function compareVersions(first, second) {
    for (let index = 0; index < first.length; index++) {
        if (first[index] !== second[index]) return first[index] < second[index] ? -1 : 1;
    }
    return 0;
}
