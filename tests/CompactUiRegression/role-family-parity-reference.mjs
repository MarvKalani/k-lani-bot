import { roleFamily } from '../../modules/desktop-ui-mcp/projection.mjs';
import { controlType } from '../../modules/desktop-ui-mcp/form-scanner.mjs';
import { fieldType } from '../../modules/desktop-ui-mcp/form-runtime.mjs';
import { isPlainTextEntry } from '../../modules/desktop-ui-mcp/role-vocabulary.mjs';

// The desktop module reports the same family, discovery field kind, form field kind and plain-text-entry rule for each platform role
// name as the native gateway.
export function roleFamilyFixtures(corpus) {
    return corpus.cases.map(entry => ({ platform: entry.platform, role: entry.role,
        family: roleFamily(entry.role), fieldKind: controlType(entry.role), formKind: fieldType(entry.role) ?? 'none',
        plainTextEntry: isPlainTextEntry(entry.role) }));
}
