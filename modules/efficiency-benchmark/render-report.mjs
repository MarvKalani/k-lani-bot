import fs from 'node:fs';
import path from 'node:path';

// Public output contains only synthetic benchmark measurements. Raw protocol frames, machine paths and runtime credentials
// remain private; unfavorable cases, failures and missing breakdowns are visible rather than converted into savings claims.
const report = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (report.trials.some(trial => trial.route === 'computer-use')) {
    throw new Error('Legacy surrogate runs are not authentic Computer Use evidence; do not publish them as acceptance.');
}
const destination = path.resolve(process.argv[3]);
fs.mkdirSync(destination, { recursive: true });
const labels = { read_setup: 'Kleine Oberfläche einmal lesen', edit_once: 'Ein Feld ändern und prüfen',
    edit_batch_5: 'Fünf Änderungen nacheinander prüfen' };
const routes = { 'mcp-cartridge': 'MCP + Cartridge', 'screenshot-adapter': 'Eigener Screenshot-Adapter' };
const publicTrials = report.trials.map(trial => ({ task: trial.task, route: trial.route, repetition: trial.repetition,
    success: trial.success, elapsedMs: trial.elapsedMs, usage: trial.usage, oracle: trial.oracle,
    toolCalls: trial.metrics.calls.length, toolErrors: trial.metrics.calls.filter(call => call.failed).length,
    schemaBytes: trial.metrics.schemaBytes, functionSchemaBytes: trial.metrics.functionSchemaBytes,
    resultJsonBytes: trial.metrics.calls.reduce((sum, call) => sum + call.resultJsonBytes, 0),
    imageCount: trial.metrics.calls.reduce((sum, call) => sum + call.imageCount, 0),
    imageBytes: trial.metrics.calls.reduce((sum, call) => sum + call.imageBytes, 0),
    imageTokens: null, toolSchemaTokens: null }));
const publication = { version: 1, createdAt: report.createdAt, model: report.model, reasoningEffort: report.reasoningEffort,
    modelClient: report.modelClient, scope: report.scope, fixtureSha256: report.fixtureSha256,
    fixtureAssemblySha256: report.fixtureAssemblySha256, nodeAssemblySha256: report.nodeAssemblySha256,
    authoring: report.authoring, groups: report.groups, trials: publicTrials };
fs.writeFileSync(path.join(destination, 'measurements.json'), JSON.stringify(publication, null, 2));
const columns = ['task', 'route', 'repetition', 'success', 'input_tokens', 'cached_input_tokens', 'output_tokens',
    'elapsed_ms', 'tool_calls', 'tool_errors', 'schema_bytes', 'function_schema_bytes', 'result_json_bytes', 'images', 'image_bytes'];
const lines = publicTrials.map(trial => [trial.task, trial.route, trial.repetition, trial.success, trial.usage?.input_tokens,
    trial.usage?.cached_input_tokens, trial.usage?.output_tokens, Math.round(trial.elapsedMs), trial.toolCalls, trial.toolErrors,
    trial.schemaBytes, trial.functionSchemaBytes, trial.resultJsonBytes, trial.imageCount, trial.imageBytes].join(','));
fs.writeFileSync(path.join(destination, 'measurements.csv'), [columns.join(','), ...lines].join('\n') + '\n');

function number(value) {
    return value === null || value === undefined ? 'Nicht verfügbar' : Math.round(value).toLocaleString('de-DE');
}

function rows() {
    return report.groups.map(group => `<tr><td>${labels[group.task]}</td><td>${routes[group.route]}</td>`
        + `<td>${group.successes}/${group.trials}</td><td>${number(group.medianInputTokens)}</td>`
        + `<td>${number(group.medianUncachedInputTokens)}</td><td>${number(group.medianOutputTokens)}</td>`
        + `<td>${(group.medianElapsedMs / 1000).toFixed(1)} s</td><td>${group.images}</td><td>${group.toolErrors}</td></tr>`).join('');
}

const html = `<!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>k-lani-bot · Effizienz-Pilotmessung</title><style>
body{margin:0;background:#101b2b;color:#edf4fd;font:16px/1.6 system-ui,sans-serif}main{max-width:1200px;margin:auto;padding:40px 24px}
h1{font-size:34px;line-height:1.2}h2{font-size:23px;margin-top:32px}a{color:#77dec7}p{max-width:950px}.badge{color:#ffd18d}
.table{overflow:auto;border:1px solid #42546d;border-radius:12px}table{border-collapse:collapse;width:100%;white-space:nowrap}
th,td{padding:14px;text-align:right;border-bottom:1px solid #304457}th:first-child,td:first-child,td:nth-child(2){text-align:left}
th{background:#213247}details{padding:16px;background:#1c2c40;border-radius:12px;margin:12px 0}code{overflow-wrap:anywhere}
</style><main><p class="badge">Pilot · synthetische GTK-Anwendung · keine Outlook-Abnahme</p>
<h1>Diagnosetest: Cartridge und eigener Screenshot-Adapter</h1>
<p>Dies ist kein Vergleich gegen authentisches Computer Use. Der Vergleichsweg ist unser eigener MCP-Screenshot-Adapter. Die Messung vergleicht gezielte Zugriffe über den Objektbaum und lokale Rezepte mit Screenshots, Mauskoordinaten und Tastatur.
Beide Routen verwenden denselben MCP-Transport, dasselbe Modell (${report.model}, ${report.reasoningEffort}) und dieselben Aufgaben.
Der mögliche Vorteil entsteht durch die Art der Bedienung und den Informationsumfang, nicht durch den Transportnamen.</p>
<p>Jeder Durchlauf startet mit einer frischen Modell-Sitzung. Katalog, Info, Schema-Ermittlung und Fehler werden mitgezählt.
Die Reihenfolge wird zwischen den Wiederholungen gewechselt. Die Oberfläche läuft in einer privaten X11-Sitzung;
gedruckt, gelöscht oder an echten Maildaten gearbeitet wird nicht.</p>
<div class="table"><table><thead><tr><th>Aufgabe</th><th>Route</th><th>Erfolg</th><th>Input · Median</th><th>Ungecacht · Median</th>
<th>Output · Median</th><th>Zeit · Median</th><th>Bilder · gesamt</th><th>Toolfehler · gesamt</th></tr></thead><tbody>${rows()}</tbody></table></div>
<p>Input/Output sind vom Modellclient gemeldete Tokens. Input zählt die über den gesamten Durchlauf aufsummierten Modellanfragen;
derselbe Kontext kann mehrfach eingehen. Caching wird deshalb separat ausgewiesen. Fehlversuche bleiben in den Summen und Erfolgsraten.
Ein unabhängiger Prüfer kontrolliert die exakten gelesenen Werte und die tatsächlich angewendete Änderungsfolge.</p>
<h2>Grenzen dieser Messung</h2>
<p>Drei Wiederholungen pro Aufgabe und Route sind ein Pilot, kein allgemeiner Beleg für alle Programme oder Einsatzbedingungen.
Ein einzelner Leseschritt kann durch Cartridge-Einrichtung teurer werden. Erfolge und Fehler müssen gemeinsam beurteilt werden.
Für Outlook fehlt weiterhin eine isolierte Windows-Testumgebung.</p>
<details><summary>Tokens, Datenumfang und Erstellungsaufwand</summary>
<p>Separate Bild- und Werkzeug-Schema-Tokens werden vom verwendeten CLI nicht berichtet und stehen als unbekannt in den Daten.
Bildanzahl, Bildbytes, sichtbare Schema-Bytes und Ergebnis-JSON-Bytes wurden gemessen; Bytes werden nicht als Tokens ausgegeben.</p>
<p>Framework- und Cartridge-Erstellung gehören nicht zur Routineausführung. Ihre Modell-Tokens wurden in dieser Entwicklung
nicht isoliert erfasst; deshalb gibt es keinen behaupteten Amortisationspunkt und keine vollständige Gesamtkostenrechnung.</p></details>
<details><summary>Versionen und Wiederholbarkeit</summary><p>Client: ${report.modelClient}. Bericht: ${report.createdAt}.</p>
<p>Fixture-Assembly SHA-256: <code>${report.fixtureAssemblySha256}</code></p>
<p>Node-Assembly SHA-256: <code>${report.nodeAssemblySha256}</code></p>
<p>Die Linux-Fensterschnittstelle meldet die Anwendungs-Version als unbekannt. Der Test bindet die gestartete Fixture-PID und
prüft die hier dokumentierten Binärdateien. Das ist keine allgemeine Freigabe der Selektoren für andere Programme.</p></details>
<p><a href="measurements.csv" download>Alle Durchläufe als CSV</a> · <a href="measurements.json" download>Messdaten als JSON</a></p>
</main></html>`;
fs.writeFileSync(path.join(destination, 'report.html'), html);
console.log(JSON.stringify({ trials: publicTrials.length, destination }));
