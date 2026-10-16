import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

// Authoring turns captured official object-reference tables into a bounded host inventory. API discovery does not
// implement the listed members; mappings below describe only the actual recipes and their restricted contexts.
const referenceDirectory = process.argv[2];
const bundleDirectory = process.argv[3];
if (!referenceDirectory || !bundleDirectory) {
    throw new Error('Usage: node scripts/build-outlook-coverage.mjs <captured references> <bundle>');
}
const mappings = new Map();
const requirements = [];

function mapMember(object, member, recipes, implementation, note) {
    mappings.set(object + '.' + member.toLowerCase(), { recipes, implementation, note });
}

mapMember('application', 'Version', ['outlook_identity'], 'Implemented', 'Read through the bound running Outlook instance.');
mapMember('application', 'LanguageSettings', ['outlook_identity'], 'Partial', 'Only the UI language identifier is returned.');
mapMember('application', 'CreateItem', ['outlook_create_draft'], 'Partial', 'Only a saved mail draft is supported.');
mapMember('application', 'ActiveExplorer', ['outlook_read_selected'], 'Partial', 'Used internally for one selected saved mail.');
for (const member of ['Delete', 'Save', 'Move', 'Display']) {
    mapMember('mailitem', member, ['outlook_' + member.toLowerCase()], 'Implemented',
        'Requires an observed item, store and expected source folder; real-application acceptance is separate.');
}
mapMember('mailitem', 'Close', ['outlook_close'], 'Partial', 'Only discard-unsaved-changes mode is supported.');
for (const member of ['Subject', 'Body', 'HTMLBody', 'To', 'CC', 'BCC', 'Importance', 'Sensitivity']) {
    const recipes = ['outlook_set_' + member.toLowerCase()];
    if (member === 'Subject' || member === 'Body') recipes.push('outlook_read_selected');
    mapMember('mailitem', member, recipes, 'Partial',
        'Saved mail context only; setters save the item. Body reads are explicitly bounded to 32768 characters.');
}
mapMember('mailitem', 'ReadReceiptRequested', ['outlook_set_read_receipt'], 'Partial', 'Setter only; a generic property read is missing.');
mapMember('mailitem', 'OriginatorDeliveryReportRequested', ['outlook_set_delivery_receipt'], 'Partial',
    'Setter only; a generic property read is missing.');
for (const member of ['EntryID', 'SenderName']) {
    mapMember('mailitem', member, ['outlook_read_selected'], 'Partial', 'One selected saved mail only.');
}
for (const member of ['Name', 'EntryID', 'StoreID']) {
    mapMember('folder', member, ['outlook_folders', 'outlook_mailboxes'], 'Partial', 'Returned in one bounded folder-tree page.');
}
mapMember('folder', 'Folders', ['outlook_folders'], 'Partial', 'Paged children only; create, rename and delete are missing.');
mapMember('namespace', 'Stores', ['outlook_mailboxes'], 'Partial', 'Only mailbox root identity and child availability are returned.');
mapMember('namespace', 'GetFolderFromID', ['outlook_folders'], 'Partial', 'Used for bounded folder traversal.');
mapMember('namespace', 'GetItemFromID', ['outlook_save', 'outlook_move'], 'Partial', 'Used internally; a generic item read is missing.');
mapMember('explorer', 'WindowState', ['outlook_window_maximized', 'outlook_window_minimized', 'outlook_window_normal'], 'Partial',
    'Native window operations only; a separate current-state recipe is missing.');

function topicFor(object, kind, member) {
    if (kind === 'Event') return 'Programming/Events';
    if (object === 'application') return 'Info/Application';
    if (['account', 'store', 'namespace'].includes(object)) return 'Administration/Accounts';
    if (['rule', 'rules'].includes(object)) return 'Administration/Rules';
    if (['appointmentitem', 'meetingitem', 'reminder', 'reminders'].includes(object)) return 'Use/Calendar';
    if (object === 'contactitem') return 'Use/Contacts';
    if (object === 'taskitem') return 'Use/Tasks';
    if (['attachment', 'attachments'].includes(object)) return 'Use/Mail/Attachments';
    if (['recipient', 'recipients'].includes(object)) return 'Use/Mail/Recipients';
    if (['folder', 'items'].includes(object)) return 'Use/Mail/Folders';
    if (['explorer', 'inspector', 'view'].includes(object)) return 'Use/Views';
    if (object === 'search') return 'Use/Mail/Search';
    if (kind === 'Method' && ['Move', 'Delete', 'Archive'].includes(member)) return 'Use/Mail/Organize';
    if (kind === 'Property' && mappings.has(object + '.' + member.toLowerCase())) return 'Use/Mail/Edit';
    return 'Use/Mail/Read';
}

function readSection(html, object, section, kind) {
    const marker = '<h2 id="' + section + '">';
    const start = html.indexOf(marker);
    if (start < 0) return;
    const end = html.indexOf('<h2', start + marker.length);
    const table = html.slice(start, end < 0 ? html.length : end);
    const anchors = table.matchAll(/<a href="([^"]+)"[^>]*>([^<]+)<\/a>/g);
    const seen = new Set();
    for (const anchor of anchors) {
        if (!anchor[1].toLowerCase().startsWith('outlook.' + object + '.')) continue;
        const member = anchor[2].trim();
        if (seen.has(member)) continue;
        seen.add(member);
        const mapped = kind === 'Event' ? null : mappings.get(object + '.' + member.toLowerCase());
        requirements.push({
            Id: object + '.' + kind.toLowerCase() + '.' + member.toLowerCase(),
            Topic: topicFor(object, kind, member),
            Name: object + '.' + member,
            Kind: kind,
            Required: kind !== 'Event',
            Source: new URL(anchor[1], 'https://learn.microsoft.com/en-us/office/vba/api/outlook.' + object).href,
            Implementation: mapped?.implementation ?? 'Missing',
            Recipes: mapped?.recipes ?? [],
            Note: mapped?.note ?? (kind === 'Event' ? 'Optional event subscription; no cartridge subscription recipe exists.'
                : 'Documented member; no executable mapping exists. Read/write details and installed-build applicability need review.')
        });
    }
}

for (const file of fs.readdirSync(referenceDirectory).filter(name => name.endsWith('.html')).sort()) {
    const object = path.basename(file, '.html');
    const html = fs.readFileSync(path.join(referenceDirectory, file), 'utf8');
    readSection(html, object, 'methods', 'Method');
    readSection(html, object, 'properties', 'Property');
    readSection(html, object, 'events', 'Event');
}

function workflow(id, topic, name, recipes = [], implementation = 'Missing', note = '') {
    requirements.unshift({Id: 'workflow.' + id, Topic: topic, Name: name, Kind: 'Workflow', Required: true,
        Source: 'operator://outlook-full-remote-control', Implementation: implementation, Recipes: recipes, Note: note});
}

workflow('identity', 'Info/Identity', 'Identify Classic vs new Outlook, build, platform, language and access',
    ['outlook_identity'], 'Partial', 'Classic only. Windows file metadata remains available when COM access fails.');
workflow('program-status', 'Use/Program', 'Check native process status, including a process without a visible window',
    ['outlook_identity'], 'Partial', 'cartridge_info probes native status when there is no window; no independent status recipe exists.');
workflow('program-start', 'Use/Program', 'Start Outlook if absent and verify its running identity',
    ['outlook_start', 'outlook_identity'], 'Partial', 'Start needs process permission and the executable allow-list; fresh Info is required.');
workflow('program-stop', 'Use/Program', 'Close the complete application and handle unsaved data');
workflow('window-states', 'Use/Window', 'Maximize, minimize and restore the selected Outlook window',
    ['outlook_window_maximized', 'outlook_window_minimized', 'outlook_window_normal'], 'Implemented');
workflow('mail-read', 'Use/Mail/Read', 'Read complete mail with stable identity and explicit limits',
    ['outlook_read_selected', 'outlook_read_mail'], 'Partial', 'Body chunks can be continued; attachment extraction is still missing.');
workflow('mail-select', 'Use/Mail/Read', 'Select a mail by stable identity and navigate to the next item');
workflow('mail-search', 'Use/Mail/Search', 'Search, filter and page mails within the correct mailbox');
workflow('mail-create', 'Use/Mail/Create', 'Create and save a synthetic draft with subject and body', ['outlook_create_draft'], 'Implemented');
workflow('mail-reply', 'Use/Mail/Reply', 'Reply, reply all and forward without losing thread context');
workflow('mail-send', 'Use/Mail/Send', 'Review recipients and attachments, send and verify the result', [], 'Missing',
    'Sending is deliberately absent from the current backend; the full-control requirement remains recorded.');
workflow('mail-revert', 'Use/Mail/Edit', 'Undo and revert edits with an explicit saved or unsaved state');
workflow('mail-options', 'Use/Mail/Edit/Options', 'Set format, priority, sensitivity, receipts, delay and voting options',
    ['outlook_set_importance', 'outlook_set_sensitivity', 'outlook_set_read_receipt', 'outlook_set_delivery_receipt'], 'Partial');
workflow('attachments', 'Use/Mail/Attachments', 'List, open, add, remove and save attachments');
workflow('recipients', 'Use/Mail/Recipients', 'Resolve and verify To, CC, BCC and address-book recipients',
    ['outlook_set_to', 'outlook_set_cc', 'outlook_set_bcc'], 'Partial', 'Text setters exist; address resolution and verification are missing.');
workflow('folders', 'Use/Mail/Folders', 'List, create, rename, move and delete mailbox folders', ['outlook_mailboxes', 'outlook_folders'], 'Partial');
workflow('mail-move', 'Use/Mail/Organize', 'Move a bound mail to an identified folder and retain its new identity', ['outlook_move'], 'Implemented');
workflow('sorting', 'Use/Mail/Sort', 'Review Laya proposals and move mail to Werbung or Unwichtig',
    ['outlook_inbox', 'outlook_list_mail', 'outlook_read_mail', 'outlook_classify_mail', 'outlook_move'], 'Partial',
    'Unwichtig means automatic notices without required action. Laya proposals need independent review; calibration and token acceptance are pending.');
workflow('labels', 'Use/Mail/Organize', 'Read/unread, flags, categories, archive and junk-mail handling');
workflow('calendar', 'Use/Calendar', 'Create, edit, delete and navigate appointments, meetings, recurrence and time zones');
workflow('contacts', 'Use/Contacts', 'Create, edit, delete and search contacts and contact groups');
workflow('tasks', 'Use/Tasks', 'Create, edit, assign and complete tasks and reminders');
workflow('accounts', 'Administration/Accounts', 'Accounts, profiles, stores, synchronization and connection settings');
workflow('rules', 'Administration/Rules', 'Inspect, create, edit, enable and verify rules');
workflow('preferences', 'Administration/Preferences', 'Signatures, templates, automatic replies, defaults and language');
workflow('permissions', 'Administration/Permissions', 'Delegation, shared mailboxes, permissions and security options');
workflow('views', 'Use/Views', 'Navigation panes, reading panes, sorting, columns, grouping and custom views');
workflow('programming', 'Programming/API', 'Discover installed native types, members and parameter metadata',
    ['outlook_api_types', 'outlook_api_members'], 'Implemented', 'Discovery is read-only; it does not implement or accept those members.');
workflow('programming-execution', 'Programming/Automation', 'Programmable application operations and explicit script execution boundaries');
workflow('ui-inventory', 'Info/UI', 'Inventory every relevant button, field, checkbox, menu, dialog and semantic action',
    ['outlook_inspect_ui', 'outlook_find_controls'], 'Partial', 'Bounded discovery exists; complete contextual inventory and semantic actions are missing.');

for (const context of ['Inbox', 'Selected mail', 'Mail editor', 'Attachment dialog', 'Recipients dialog', 'Mail options',
    'Folder management', 'Search and filters', 'Calendar and meeting editor', 'Contacts editor', 'Task editor',
    'Accounts and profiles', 'Rules editor', 'Preferences and security', 'Modal dialogs and error recovery']) {
    requirements.unshift({Id:'ui.' + context.toLowerCase().replaceAll(' ', '-'), Topic:'Info/UI/Contexts', Name:context,
        Kind:'UiContext', Required:true, Source:'operator://every-button-and-field', Implementation:'Missing', Recipes:[],
        Note:'Every available control needs its identity, action, value type, context, route and verified outcome recorded.'});
}

const document = {Format:'k-lani-bot.cartridge-coverage.v1', ReferenceComplete:false,
    PendingReviews:['Complete user-guide review', 'Installed-build native API reconciliation', 'All contextual UI controls',
        'Actual read/write capabilities', 'Real-application recipe acceptance', 'Matched successful-task token benchmark'],
    Requirements:requirements};
const bytes = Buffer.from(JSON.stringify(document,null,2)+'\n');
fs.writeFileSync(path.join(bundleDirectory,'coverage.json'),bytes);
const manifestPath = path.join(bundleDirectory,'cartridge.json');
const manifest = JSON.parse(fs.readFileSync(manifestPath,'utf8'));
manifest.coverage = 'coverage.json';
manifest.files = manifest.files.filter(file => file.path !== 'coverage.json');
manifest.files.push({path:'coverage.json',bytes:bytes.length,sha256:crypto.createHash('sha256').update(bytes).digest('hex')});
fs.writeFileSync(manifestPath,JSON.stringify(manifest,null,2)+'\n');
process.stdout.write(JSON.stringify({requirements:requirements.length,required:requirements.filter(item=>item.Required).length,
    bytes:bytes.length,referenceComplete:false})+'\n');
