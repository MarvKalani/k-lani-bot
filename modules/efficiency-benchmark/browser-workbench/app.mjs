const element = id => document.getElementById(id);
let requestId = 1;
let renderedDocument;
let renderedFiles;
let renderedAmounts;
const session = new URL(location.href).searchParams.get('session') ?? 'mcp';
const endpoint = pathname => `${pathname}?session=${encodeURIComponent(session)}`;

async function invoke(name, argumentsForTool) {
    const response = await fetch(endpoint('/mcp'), { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ jsonrpc: '2.0', id: requestId++, method: 'tools/call',
            params: { name, arguments: argumentsForTool } }) });
    const message = await response.json();
    if (message.error || message.error === '') throw new Error(message.error.message ?? message.error);
    if (!response.ok) throw new Error(message.error ?? 'Request failed.');
    await refresh();
    return JSON.parse(message.result.content[0].text);
}

async function call(name, argumentsForFunction) {
    return invoke('cartridge_call', { function: name, arguments: argumentsForFunction });
}

function showPanel(name) {
    for (const button of document.querySelectorAll('[data-panel]')) {
        button.setAttribute('aria-selected', String(button.dataset.panel === name));
    }
    for (const panel of ['calculator', 'editor', 'files']) element(panel).hidden = panel !== name;
}

async function refresh() {
    const state = await (await fetch(endpoint('/state'))).json();
    showTask(state.task);
    if (renderedDocument !== state.document) {
        element('document').value = state.document;
        renderedDocument = state.document;
    }
    element('total').textContent = state.calculation ? `Summe: ${state.calculation.total} EUR` : 'Noch keine Berechnung';
    const amounts = state.calculation ? state.calculation.values.join('\n') : '';
    if (renderedAmounts !== amounts) {
        element('amounts').value = amounts;
        renderedAmounts = amounts;
    }
    element('events').textContent = state.events.map(event => `${event.step}. ✓ ${event.function}\n${JSON.stringify(event.result)}`).join('\n\n');
    if (state.openedFile) {
        const result = state.openedFile;
        element('file-content').textContent = `${result.path}\n${result.bytes} Bytes · SHA-256 ${result.sha256}\n\n${result.text}`;
    }
    const signature = JSON.stringify(state.files);
    if (renderedFiles === signature) return;
    renderedFiles = signature;
    element('file-list').replaceChildren();
    for (const file of state.files) {
        const listItem = document.createElement('li');
        const button = document.createElement('button');
        button.textContent = `Öffnen: ${file}`;
        button.onclick = () => safely(async () => {
            const [, folder, filename] = file.split('/');
            const result = await call('file.read', { folder, file: filename });
            element('file-content').textContent = `${result.path}\n${result.bytes} Bytes · SHA-256 ${result.sha256}\n\n${result.text}`;
        });
        listItem.append(button);
        element('file-list').append(listItem);
    }
}

async function safely(action) {
    try {
        element('notice').textContent = '';
        await action();
    } catch (error) {
        element('notice').textContent = error.message;
    }
}

for (const button of document.querySelectorAll('[data-panel]')) button.onclick = () => showPanel(button.dataset.panel);
element('calculate').onclick = () => safely(() => call('calculator.sum', {
    values: element('amounts').value.trim().split(/\s+/) }));
element('write').onclick = () => safely(() => call('editor.write', { text: element('document').value }));
element('create-folder').onclick = () => safely(async () => {
    const result = await call('folder.create', { folder: element('new-folder').value });
    element('notice').textContent = `Ordner angelegt: ${result.folder}`;
});
element('save').onclick = () => safely(async () => {
    await call('editor.write', { text: element('document').value });
    const result = await call('file.save', { folder: element('save-folder').value, file: element('save-file').value });
    element('saved').textContent = `${result.path} · ${result.bytes} Bytes · gespeichert`;
});

async function registerTools() {
    if (session === 'computer-use') {
        element('mcp-status').textContent = 'Computer-Use-Testsitzung: Hier wird die Oberfläche bedient.';
        return;
    }
    const context = document.modelContext ?? navigator.modelContext;
    if (!context) {
        element('mcp-status').textContent = 'WebMCP ist in diesem Browser nicht verfügbar. Standard-MCP über stdio ist vorbereitet.';
        return;
    }
    const message = await (await fetch(endpoint('/mcp'), { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ jsonrpc: '2.0', id: requestId++, method: 'tools/list' }) })).json();
    for (const definition of message.result.tools) {
        await context.registerTool({ ...definition,
            execute: async argumentsForTool => JSON.stringify(await invoke(definition.name, argumentsForTool)),
            annotations: { readOnlyHint: definition.name !== 'cartridge_call' } });
    }
    element('mcp-status').textContent = 'Drei WebMCP-Werkzeuge aktiv. Funktionsschemas werden thematisch geladen; neue Abläufe sofort verfügbar.';
}

function showTask(task) {
    if (!task) return;
    element('task').hidden = false;
    element('task-values').textContent = `Beträge: ${task.values.join(' + ')} EUR. Addiere sie im Rechner.`;
    element('task-file').textContent = `Ersetze {{amounts}} durch die Einzelbeträge, mit „, “ getrennt, und {{total}} durch die Summe. `
        + `Speichere als ${task.path} und öffne die gespeicherte Datei erneut.`;
    element('task-template').textContent = task.htmlTemplate;
}

async function refreshComparison() {
    const result = await (await fetch('/comparison/result')).json();
    element('comparison-task').textContent = `Auftrag: ${result.task.values.join(' + ')} EUR addieren und den HTML-Bericht `
        + `unter ${result.task.path} speichern und wieder öffnen.`;
    for (const name of ['computer-use', 'mcp']) {
        const entry = result.sessions[name];
        const label = entry.status === 'passed' ? 'Bestanden' : entry.status === 'failed' ? 'Prüfung fehlgeschlagen' : 'Noch nicht abgeschlossen';
        element(`${name}-result`).textContent = `${label} · ${entry.completedOperations} abgeschlossene Programmschritte`
            + (entry.sha256 ? ` · ${entry.bytes} Bytes\nSHA-256 ${entry.sha256}` : '');
        element(`${name}-result`).className = `result-box ${entry.status}`;
    }
    element('comparison-verdict').textContent = result.equivalent ? 'Beide Aufgaben bestanden · Dateien bytegleich'
        : result.status === 'failed' ? 'Ergebnisse erfüllen den gemeinsamen Auftrag noch nicht.' : 'Beide vollständigen Ergebnisse stehen noch aus.';
    element('comparison-verdict').className = result.status;
    element('comparison-details').textContent = JSON.stringify(result, null, 2);
}

async function start() {
    if (window.self !== window.top) document.body.classList.add('embedded');
    if (location.pathname === '/comparison') {
        document.body.classList.add('comparison-page');
        element('comparison').hidden = false;
        element('application').hidden = true;
        element('computer-use-frame').src = '/?session=computer-use';
        element('mcp-frame').src = '/?session=mcp';
        element('verify-comparison').onclick = () => safely(refreshComparison);
        await refreshComparison();
        setInterval(() => refreshComparison().catch(() => {}), 2500);
        return;
    }
    await refresh();
    await safely(registerTools);
    setInterval(() => refresh().catch(() => {}), 2500);
}

await start();
