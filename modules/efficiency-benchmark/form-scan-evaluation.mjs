// The extraction oracle compares the requested fields exactly, including duplicates and explicit unknown rules.
// Paired captures must contain the same task information; asynchronous UI changes cannot become apparent model errors.
export function expectedFormInventory(compact) {
    const fields = compact.elements.filter(element => /\bEdit\b|entry/i.test(element.role)).map(element => ({ name: element.name,
        value: element.value ?? null, enabled: !element.disabled, offscreen: Boolean(element.offscreen), required: null }));
    const buttons = compact.elements.filter(element => /^(button|push button)$/i.test(element.role)).map(element => ({ name: element.name,
        enabled: !element.disabled, offscreen: Boolean(element.offscreen), effect: null }));
    return { fields, buttons };
}

export function formInventoryMatches(answer, expected) {
    if (!answer || !Array.isArray(answer.fields) || !Array.isArray(answer.buttons)) return false;
    if (Object.keys(answer).some(key => !['fields', 'buttons'].includes(key))) return false;

    return JSON.stringify(normalizeInventory(answer)) === JSON.stringify(normalizeInventory(expected));
}

export function validatePairedCapture(capture) {
    if (capture.compact.truncated || capture.compact.elements.some(element => element.name_truncated || element.value_truncated)
        || capture.scanned.fields.some(field => field.name_truncated)
        || capture.scanned.contents.fields.some(field => field.value_truncated)) throw new Error('Use complete bounded observations for this extraction task.');
    const scanned = capture.scanned;
    const fields = scanned.fields.filter(field => /\bEdit\b|entry/i.test(field.role)).map(field => ({ name: field.name,
        value: scanned.contents.fields.find(content => content.key === field.key)?.value ?? null,
        enabled: field.enabled, offscreen: field.offscreen, required: null }));
    const buttons = scanned.fields.filter(field => /^(button|push button)$/i.test(field.role)).map(field => ({ name: field.name,
        enabled: field.enabled, offscreen: field.offscreen, effect: null }));
    if (!formInventoryMatches({ fields, buttons }, expectedFormInventory(capture.compact))) {
        throw new Error('Paired observations changed between acquisition routes; recapture a stable state.');
    }
}

function normalizeInventory(answer) {
    return { fields: orderedRecords(answer.fields), buttons: orderedRecords(answer.buttons) };
}

function orderedRecords(values) {
    return values.map(value => Object.fromEntries(Object.entries(value).sort(([first], [second]) => first.localeCompare(second))))
        .sort((first, second) => JSON.stringify(first).localeCompare(JSON.stringify(second)));
}
