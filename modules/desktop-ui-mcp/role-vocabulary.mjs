// Exact platform role names whose broad pattern classification is wrong. A listed name takes its entry from this table;
// every other name keeps the pattern result. The native gateway lists the same names in CompactRoleVocabulary.cs.
const linuxRoles = [];
const windowsRoles = [
    { role: 'MenuItem', family: 'Interactive', fieldKind: 'action' },
    { role: 'ListItem', family: 'Interactive', fieldKind: null },
    { role: 'TreeItem', family: 'Interactive', fieldKind: null }
];
const macOsRoles = [
    { role: 'TextField', family: 'Editable', fieldKind: 'text' },
    { role: 'TextArea', family: 'Editable', fieldKind: 'text' },
    { role: 'PopUpButton', family: 'Interactive', fieldKind: 'choice' },
    { role: 'MenuItem', family: 'Interactive', fieldKind: 'action' },
    { role: 'SecureTextField', family: 'Other', fieldKind: null }
];
const vocabulary = [...linuxRoles, ...windowsRoles, ...macOsRoles];

// Platform vocabularies differ in capitalisation, so names are compared without regard to case.
export function roleVocabularyEntry(role) {
    const normalizedRole = role.toLowerCase();
    const entry = vocabulary.find(candidate => candidate.role.toLowerCase() === normalizedRole);
    return entry ? { family: entry.family, fieldKind: entry.fieldKind } : null;
}

// Only an Edit or entry name, or a vocabulary text field such as TextArea, takes plain typed text.
// A password never qualifies, even when its name matches.
export function isPlainTextEntry(role) {
    if (/password/i.test(role)) return false;
    const entry = roleVocabularyEntry(role);
    if (entry) return entry.family === 'Editable' && entry.fieldKind === 'text';

    return /\bEdit\b|entry/i.test(role);
}
