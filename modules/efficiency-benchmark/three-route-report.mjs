import fs from 'node:fs';
import path from 'node:path';
import { summarizeThreeRoutes } from './three-route-summary.mjs';

const report = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const destination = path.resolve(process.argv[3]);
const summary = report.format === 'k-lani-bot-three-route-summary' ? report : summarizeThreeRoutes(report);
const labels = { 'computer-use': 'Computer Use · Objektbaum', 'mcp-cartridge': 'Unser Test-MCP · ohne Bilder',
    'bot-screenshots': 'k-lani-bot · Nur Screenshots', 'chrome-devtools': 'Chrome DevTools MCP',
    'bot-hybrid': 'k-lani-bot · Voller Fensterbaum + Bilder', 'bot-compact': 'k-lani-bot · Kompakter UI-Zugang' };
const colors = { 'computer-use': '#77b5ff', 'mcp-cartridge': '#78dfc9', 'bot-screenshots': '#ffc46b',
    'chrome-devtools': '#bd9aff', 'bot-hybrid': '#fb94b5', 'bot-compact': '#b4e486' };
const number = value => value === null || value === undefined ? 'Unbekannt' : Math.round(value).toLocaleString('de-DE');
const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('"', '&quot;');
const completed = summary.groups.every(group => group.successes >= 3 && group.unmeasuredTrials === 0);

function average(group, field) {
    return group.successes > 0 && group[field] !== null ? group[field] / group.successes : null;
}

function rows() {
    return summary.groups.map(group => `<tr><td>${labels[group.route]}</td><td>${group.successes}/${group.trials}</td>`
        + `<td>${number(average(group, 'totalInputTokens'))}</td><td>${number(average(group, 'totalCachedInputTokens'))}</td>`
        + `<td>${number(average(group, 'totalOutputTokens'))}</td><td>${number(group.totalTokensPerSuccess)}</td>`
        + `<td>${group.images}</td><td>${group.detectedToolErrors}</td></tr>`).join('');
}

function attempts() {
    return summary.trials.map(trial => `<tr><td>${trial.repetition + 1}</td><td>${labels[trial.route]}</td><td>${trial.attempt + 1}</td>`
        + `<td>${trial.success ? 'Bestanden' : 'Nicht abgeschlossen'}</td><td>${number(trial.usage?.input_tokens)}</td>`
        + `<td>${number(trial.usage?.cached_input_tokens)}</td><td>${number(trial.usage?.output_tokens)}</td>`
        + `<td>${number(trial.toolCalls)}</td><td>${number(trial.images)}</td><td>${(trial.elapsedMs / 1000).toFixed(1)} s</td></tr>`).join('');
}

function savings() {
    return summary.pairedSavings.map(entry => `<p>MCP gegenüber ${labels[entry.baseline]}: `
        + `<strong>${(entry.totalTokenReduction * 100).toFixed(1)} % weniger Input + Output</strong>, `
        + `mit ${entry.pairedSuccessfulCases} gepaarten Aufgaben und allen nötigen Anläufen. `
        + `Ohne gecachte Input-Tokens: ${(entry.uncachedInputPlusOutputReduction * 100).toFixed(1)} % weniger.</p>`).join('');
}

function priceRows() {
    return (summary.apiReferencePricing?.groups ?? []).filter(Boolean).map(group => `<tr><td>${labels[group.route]}</td>`
        + `<td>$${group.usdPerSuccessReference.toFixed(6)}</td><td>$${group.totalUsdReference.toFixed(6)}</td></tr>`).join('');
}

function hybridResults() {
    const trials = summary.trials.filter(trial => trial.route === 'bot-hybrid');
    if (trials.length === 0) return '';
    const group = summary.groups.find(entry => entry.route === 'bot-hybrid');
    const price = summary.apiReferencePricing?.groups.find(entry => entry.route === 'bot-hybrid');
    const pixel = summary.groups.find(entry => entry.route === 'bot-screenshots');
    const versions = [...new Set(trials.map(trial => trial.toolServerVersion).filter(Boolean))].join(', ') || 'Unbekannt';
    const imageFree = trials.find(trial => trial.success && trial.images === 0);
    const paired = imageFree && summary.trials.find(trial => trial.route === 'computer-use' && trial.success
        && trial.repetition === imageFree.repetition && JSON.stringify(trial.dataset) === JSON.stringify(imageFree.dataset));
    const higherUsage = group.totalTokensPerSuccess !== null && pixel?.totalTokensPerSuccess !== null
        && group.totalTokensPerSuccess > pixel?.totalTokensPerSuccess;
    const details = trials.map(trial => `<tr><td>${trial.repetition + 1}</td><td>${trial.success ? 'Bestanden' : 'Offen'}</td>`
        + `<td>${trial.images}</td><td>${trial.semanticActionDispatches}</td><td>${trial.physicalClicks}</td>`
        + `<td>${trial.toolCalls}</td></tr>`).join('');
    return `<section><h2>Unser Node mit Objektbaum und optionalen Bildern</h2>
<p>k-lani-bot-node ${escape(versions)} unter Linux AT-SPI: ${group.successes} erfolgreich erledigte Aufgaben bei ${group.trials} Anläufen.
Bilder je Anlauf: ${trials.map(trial => number(trial.images)).join(', ')}, insgesamt ${group.images}.
Physische Maus-Klickaufrufe: ${trials.reduce((sum, trial) => sum + (trial.physicalClicks ?? 0), 0)}.
${imageFree ? 'Mindestens ein Lauf erledigte den gesamten Auftrag bildfrei.' : ''}</p>
<div class="table"><table><thead><tr><th>Datensatz</th><th>Ergebnis</th><th>Bilder</th><th>Semantische Aktionen</th>
<th>Physische Klickaufrufe</th><th>Werkzeugaufrufe</th></tr></thead><tbody>${details}</tbody></table></div>
<p><strong>${higherUsage ? 'Die Token-Effizienz dieser allgemeinen UI-Spur reicht noch nicht aus.' : 'Tokenverbrauch wird getrennt von Bildern bewertet.'}</strong>
${number(group.totalTokensPerSuccess)} Input + Output pro Aufgabe und $${price?.usdPerSuccessReference?.toFixed(6) ?? 'Unbekannt'}
API-Referenzkosten. ${higherUsage ? 'Der Tokenverbrauch liegt über dem reinen Bildvergleich.' : ''}
Viele einzelne Aufrufe und wiederholter Kontext bleiben teuer. Weniger Bilder allein lösen das nicht.
${paired ? `Beim bildfreien Datensatz waren es ${imageFree.toolCalls} Werkzeugaufrufe, beim zugehörigen Computer-Use-Lauf ${paired.toolCalls}.` : ''}
Das konkrete Ziel für die Cartridge sind gebündelte lokale Abläufe und kompakte Ergebnisse.</p>
<p>Vorläufe zur Reparatur der Fensterzuordnung, Aktionsauswahl und des Objektfokus sind separat gespeichert.
Die Routine-Tabelle enthält alle Anläufe der abschließenden Spur. Entwicklungsaufwand und Vorläufe werden nicht als
Routine ausgegeben. Die zusätzliche Spur wurde später erhoben; Laufzeiten sind wegen paralleler Diagnoseprozesse explorativ.
Das ist eine Linux-Browser-Abnahme, keine Prüfung eines Windows-Nodes.</p></section>`;
}

function compactResults() {
    const group = summary.groups.find(entry => entry.route === 'bot-compact');
    if (!group) return '';
    const older = summary.groups.find(entry => entry.route === 'bot-hybrid');
    const browser = summary.groups.find(entry => entry.route === 'computer-use');
    const savings = baseline => (100 * (1 - group.totalTokensPerSuccess / baseline.totalTokensPerSuccess)).toFixed(1);
    const trials = summary.trials.filter(trial => trial.route === 'bot-compact');
    const calls = trials.map(trial => trial.toolCalls).join(', ');
    const internalCalls = trials.map(trial => trial.nativeUi?.calls ?? 'Unbekannt').join(', ');
    const schemaBytes = trials[0]?.toolDescriptionsBytes;
    const price = summary.apiReferencePricing?.groups.find(entry => entry.route === 'bot-compact');
    return `<section id="compact-ui"><h2>Der kompakte Objektbaum ist jetzt gemessen</h2>
<p><strong>${group.successes}/${group.trials} Anläufe bestanden, ${group.images} Bilder ans Modell.</strong>
${number(group.totalTokensPerSuccess)} Input + Output und $${price?.usdPerSuccessReference?.toFixed(6) ?? 'Unbekannt'}
API-Referenzkosten je erledigter Aufgabe. ${older ? savings(older) + ' % weniger Tokens als unsere bisherige volle UI-Spur.' : ''}
${browser ? savings(browser) + ' % weniger als Computer Use in den gepaarten Browser-Aufgaben.' : ''}</p>
<p>Der echte Node wird weiterhin über MCP bedient. Eine neue UI-Zugangsschicht nimmt seinen nativen Baum entgegen,
beschränkt ihn auf den Anwendungszweig und liefert Änderungen statt wiederholter Vollansichten. Leere Container,
doppelte Beschriftungen, redundante Werte und Diagnosequittungen bleiben außerhalb des Modellkontexts.
Nutztexte, Ergebnisse, Dialoge und Hinweise auf gekürzte Werte bleiben erhalten. Die Sichtbarkeitsbeschränkung ist optional;
standardmäßig bleiben außerhalb des Bildschirmbereichs liegende Elemente verfügbar.</p>
<p>Drei kompakte Werkzeuge statt zehn vollständiger Primitive: ${number(schemaBytes)} statt 17.597 Bytes Beschreibung und Schema.
Modell-Werkzeugaufrufe je Aufgabe: ${calls}. Dahinter stehen ${internalCalls} native MCP-Aufrufe.
Die Arbeit verschwindet also nicht; bekannte UI-Schritte werden ohne zusätzliche Modellrunden ausgeführt.
Fensteridentität und Ziele werden frisch geprüft. Bei Mehrdeutigkeit oder unbekannter Fehlerlage wird nicht automatisch weitergeklickt.</p>
<p>Diese Schicht nutzt weder Chrome DevTools noch DOM-, Backend- oder Programmfunktionen.
Die detaillierten Node-Daten werden derzeit intern noch übertragen; eingespart wird der Kontext des Modells.
Das direkte Programm-MCP bleibt für bekannte Abläufe sparsamer. Getestet ist Linux/X11 mit Node 0.2610.0609,
keine allgemeine Windows- oder Outlook-Abnahme. Erstellung und Vorläufe werden separat geführt.</p></section>`;
}

function firstUseResults() {
    const study = summary.firstUseVisual;
    if (!study) return '';
    const names = { 'bot-screenshots': 'Pixelbedienung', 'bot-marked': 'Nummerierte native Elemente', 'bot-crops': 'Nummerierte Elemente + Ausschnitte' };
    const rows = study.groups.map(group => `<tr><td>${names[group.route]}</td><td>${group.successes}/${group.attempts}</td>`
        + `<td>${number(group.tokensPerSuccess)}</td><td>${group.images}</td><td>$${group.usdPerSuccessReference.toFixed(6)}</td>`
        + `<td>${group.generatedFunctions.join(' / ')}</td></tr>`).join('');
    return `<section id="first-use"><h2>Erste Nutzung erzeugt sofort MCP-Material</h2>
<p><strong>Automatischer Erstaufruf bestanden, ohne zusätzlichen Lernaufruf.</strong> Schreibfelder und Registerwechsel erzeugen
ihre Funktion, Parameter und Prüfkriterien direkt nach erfolgreicher Bedienung. Andere Aktionen liefern gespeicherte Kandidaten
mit Zielidentität; sie sind erst nach einer passenden Ergebnisprüfung als geprüfte Funktion aufrufbar.</p>
<p>Im Neustarttest wurden Browser und MCP-Verbindung ersetzt. Die erzeugte Funktion schrieb einen anderen Text,
prüfte ihn und lieferte ${study.automaticAcceptance.returnedBytes} Bytes Ergebnis ohne Baum oder Bild.
Die KI schreibt weder Selektoren noch Rezeptdateien. Der Host speichert eine aktive Erweiterung je Programm/Version;
Eingabetexte, Screenshots, Prozess- und Elementnummern sowie feste Klickpositionen werden nicht gespeichert.</p>
<h2>Bildvergleich mit GPT-6 Luna Medium</h2><div class="table"><table><thead><tr><th>Erster Aufgabengang</th><th>Erfolge / Anläufe</th>
<th>Tokens / erledigte Aufgabe</th><th>Bilder gesamt</th><th>API-Referenz USD / Aufgabe</th><th>Geprüfte Funktionen je Erfolg</th></tr></thead>
<tbody>${rows}</tbody></table></div>
<p>Gleiche drei Datensätze, unabhängige Datei- und Inhaltsprüfung. Fehlversuche sind mitgezählt.
Die markierten Spuren lesen Ziele aus dem nativen Baum und erzeugen zusätzlich MCP-Material; sie sind deshalb keine reine Pixelbedienung.
Die Tabelle zählt den ersten Aufgabengang einschließlich Erzeugung, keine isolierte Wiederverwendung.</p>
<p>Das Ergebnis spricht aktuell für kompakte Baumdaten und direkte Funktionen. Nummerierte Vollbilder waren in dieser Reihe
teurer als die Pixelspur. Ausschnitte verringerten die sichtbare Bildfläche im Mittel um etwa 19 %, führten aber zu mehr Kontextaufwand
und einem Fehlversuch. Das ist ein kleiner Pilot, kein allgemeines Qualitätsranking.</p>
<p>Lokale OCR erkannte 102 Wörter in einer ursprünglichen Aufnahme. Daraus folgt keine geprüfte Klickbarkeit;
ein reiner Pixel-Detektor wurde noch nicht als Bedienweg abgenommen. Separate Bildtokens und Entwicklungsverbrauch bleiben unbekannt.</p></section>`;
}

function formScannerResults() {
    const study = summary.formScannerStudy;
    if (!study) return '';
    const names = { compact: 'Kompakter UI-Baum', scanned: 'Formularinventar' };
    const rows = study.groups.map(group => `<tr><td>Luna ${escape(group.effort)}</td><td>${names[group.route]}</td>`
        + `<td>${group.successes}/${group.attempts}</td><td>${number(group.tokensPerSuccess)}</td>`
        + `<td>$${group.successes && group.usdReference !== null ? (group.usdReference / group.successes).toFixed(6) : 'Unbekannt'}</td></tr>`).join('');
    const pilot = study.mcpPilot?.groups.filter(group => group.route === 'scanned').map(group =>
        `Luna ${escape(group.effort)}: ${group.successes}/${group.attempts}`).join('; ');
    return `<section id="form-scanner"><h2>Formularscanner: Erfassung und Bedienung getrennt</h2>
<p>Der native Strukturscanner erfasst Feldidentitäten, Rollen, Typen und vorgeschlagene Aktionen lokal.
Aktuelle Werte können begrenzt gelesen werden; gespeicherte Strukturen enthalten keine Eingabewerte.
Pflichtfelder, Schreibschutz, Auswahlregeln, Auslöser versteckter Felder und Buttonwirkungen bleiben unbekannt, wenn der Baum sie nicht belegt.
Aufeinanderfolgende Beobachtungen speichern Zustandsänderungen mit unbekannter Ursache.</p>
<p><strong>Einordnungstest mit identischen aufgezeichneten nativen Ansichten.</strong> Das Modell erhält die Beobachtung direkt;
geprüft werden Textfelder, Werte, Buttons und Providerflags gegen eine unabhängige Extraktion aus dem kompakten Baum.
Drei Ansichten: Start, Editor, bearbeiteter Wert. Keine Bilder. Das ist eine kleine Inhaltsprüfung, keine vollständige Bedienungsabnahme.</p>
<div class="table"><table><thead><tr><th>Modell</th><th>Material</th><th>Exakte Inventare / Anläufe</th>
<th>Input + Output / Erfolg</th><th>API-Referenz / Erfolg</th></tr></thead><tbody>${rows}</tbody></table></div>
<p>Das reichhaltige Inventar ist größer als unser vorhandener kompakter Baum. Ein niedrigeres Denkbudget garantiert deshalb
keine Gesamtersparnis. Wiederverwendung ausführbarer Funktionen und vollständige Aufgaben müssen weiterhin separat gemessen werden.</p>
${pilot ? `<details><summary>Vorläufiger MCP-Abrufpilot</summary><p>${pilot}. Dieser getrennte Vorlauf verlangt zusätzlich den Werkzeugabruf;
mehrere Läufe endeten ohne Beobachtung oder mit falsch eingeordneten Elementen. Alle Anläufe sind erhalten.
Danach wurden native Toggle-Buttons präziser typisiert. Der Vorlauf ist kein reiner Vergleich der Denkstufen.</p></details>` : ''}
<p>Erstellung und native Erfassung benötigen in diesem Implementierungsweg keine Modellinferenz.
Entwicklungsverbrauch ist nicht isoliert gemessen. Separate Bild- und Schematokens bleiben unbekannt.</p></section>`;
}

const chartData = summary.groups.map(group => ({ name: labels[group.route], color: colors[group.route],
    all: group.totalTokensPerSuccess,
    uncached: (group.totalInputTokens - group.totalCachedInputTokens + group.totalOutputTokens) / group.successes }));
const chartJson = JSON.stringify(chartData).replaceAll('<', '\\u003c');
const html = `<!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>k-lani-bot · Gemessener Tokenvergleich</title><style>
:root{color-scheme:dark;font-family:system-ui,sans-serif;background:#101b2b;color:#eef5ff}body{margin:0}
main{max-width:1200px;margin:auto;padding:32px 24px}h1{font-size:32px;line-height:1.2}h2{font-size:23px}
p{line-height:1.6;max-width:1050px}.badge{color:#78dfc9}.muted{color:#bccbde}a{color:#78dfc9}
section,details{background:#1a2b42;border:1px solid #526780;border-radius:14px;padding:20px;margin:24px 0}
.table{overflow:auto}table{border-collapse:collapse;width:100%;white-space:nowrap}th,td{text-align:right;padding:12px;border-bottom:1px solid #526780}
th:first-child,td:first-child,td:nth-child(2){text-align:left}button{font:inherit;padding:10px 16px;border-radius:8px;background:#254257;color:inherit;border:1px solid #78dfc9;cursor:pointer}
.chart-row{margin:22px 0}.chart-label{margin-bottom:8px}.bar{height:26px;border-radius:6px;min-width:2px}.chart-number{margin-top:6px}
code{overflow-wrap:anywhere}summary{cursor:pointer;font-size:18px}
</style><main><p class="badge">${completed ? 'Alle Verfahren erledigen dieselben drei Aufgaben' : 'Messung noch nicht vollständig'}</p>
<h1>${summary.groups.length} Wege · dasselbe Modell · gemessene Tokens</h1>
<p><strong>${escape(summary.model)} · ${escape(summary.reasoningEffort)}</strong>, ${escape(summary.modelClient)}.
Aufgabe: fünf Beträge im Rechner addieren, im Editor einen HTML-Bericht erstellen, in einen neuen Testordner speichern und wieder öffnen.
Drei Datensätze, frische Anwendungzustände und Modellsitzungen, wechselnde Reihenfolge.</p>
<p>Der unabhängige Prüfer kontrolliert tatsächliche Dateien, Rechnung, exakten HTML-Inhalt, erneutes Öffnen und zusätzliche Ausgaben.</p>
<section><h2>Tokens je erfolgreich erledigter Aufgabe</h2><p>Durchschnitt über drei Aufgaben je Verfahren.
<strong>Fehlgeschlagene Anläufe sind mitgezählt.</strong> Input zählt alle Modellanfragen einschließlich wiederholt eingehendem und gecachtem Kontext.</p>
<button id="toggle">Gecachte Input-Tokens ausblenden</button><div id="chart"></div>
<div class="table"><table><thead><tr><th>Verfahren</th><th>Erfolge / Anläufe</th><th>Input / Aufgabe</th>
<th>Davon gecacht</th><th>Output / Aufgabe</th><th>Input + Output / Aufgabe</th><th>Bilder gesamt</th><th>Toolfehler erkannt</th></tr></thead><tbody>${rows()}</tbody></table></div>
${savings()}</section>
${compactResults()}
${firstUseResults()}
${formScannerResults()}
${hybridResults()}
<section><h2>Warum 64 Bilder?</h2><p>64 ist die Summe aus drei Aufgaben und einem Fehlversuch:
17, 16 und 18 Bilder in den erfolgreichen Durchläufen, plus 13 im abgebrochenen Lauf. Es waren nicht 64 Bilder für eine einzelne Aufgabe.</p>
<p>29 Aufnahmen folgten auf Scroll- oder Seitenbewegungen, 28 auf Klicks, drei auf andere Tastatureingaben und vier waren Startbilder.
Die Such- und Scrollarbeit in der Testoberfläche ist damit ein wesentlicher Teil des Aufwands. Jedes Bild wurde vom Modell angefordert;
der Node sendete kein zusätzliches Bild nach jedem Klick.</p>
<p>Computer Use und Chrome DevTools MCP verwendeten in dieser Vergleichsreihe den Objektbaum beziehungsweise DOM und sendeten
keine Bilder. Der Preisunterschied ist deshalb kein Beleg für eine doppelte Screenshot-Frequenz gegenüber einem anderen Screenshot-Verfahren.</p>
<p>Ein zusätzlicher Test mit den echten Tastatur-Makros des Nodes erledigte alle drei Aufgaben, sank bei den erfolgreichen Läufen
aber nicht in der Bildzahl: 19/17/19 statt 20/18/12. Mitgezählt wurden 55 statt 67 Bilder einschließlich Fehlversuch,
aber 4,10 statt 3,58 Millionen Input-Tokens. Das Erstellen der Makros führte zu zusätzlichen Modellrunden.
Bildanzahl allein erklärt die Kosten daher nicht.</p></section>
<section><h2>Dollar-Rechnung anhand der Modellkarte</h2><p>API-Referenzkosten für GPT-6 Luna, Standard und kurzer Anfragekontext:
$0,10 / Million Input-Tokens, $0,01 / Million gecachte Input-Tokens und $0,50 / Million Output-Tokens.</p>
<div class="table"><table><thead><tr><th>Verfahren</th><th>USD pro erledigter Aufgabe</th><th>USD gesamte Reihe</th></tr></thead><tbody>${priceRows()}</tbody></table></div>
<p><a href="https://developers.openai.com/api/docs/models/gpt-6-luna">Offizielle Modellkarte</a>, geprüft am 06.10.2026.
Die Werte enthalten Fehlversuche und Cache-Nutzung. Sie sind keine Rechnung für die Codex-Sitzungen.</p>
<p>Die Modellkarte kennt Zuschläge für lange Einzelanfragen, Fast-Modus und regionale Verarbeitung. Der Messclient liefert
aufsummierte Tokens pro Lauf; daraus lässt sich die Größe einer einzelnen Anfrage nicht ablesen. Hier ist deshalb der
Standard-Kurzkontext-Tarif eine ausdrücklich benannte Referenz.</p></section>
<section><h2>Wie die Verfahren bedient haben</h2><p>Computer Use verwendete das echte OpenAI-Browserwerkzeug mit Chrome und semantischen Elementen.
Es benötigte in dieser Reihe keine Screenshots. MCP führte das von der KI erzeugte Rezept über Programmfunktionen aus.
Die dritte Spur verwendete k-lani-bot-Screenshots, Maus, Ziehen, Tastatur und Zwischenablage ohne Cartridge oder Objektbaum-Steuerung.</p>
<p>Die bildfreie Spur verwendete unser selbst geschriebenes Test-MCP für die Browser-Werkstatt mit Rechner-, Editor- und Dateifunktionen.
Das war keine vollständige Messung der produktiven k-lani-bot-MCP-Anbindung auf einem Windows-Node. Chrome DevTools MCP ist eine eigene
Vergleichsspur mit dem offiziellen Server 1.10.1, vollständigem Standard-Werkzeugkatalog und einer isolierten Headless-Chrome-Sitzung.</p>
<p>Der Codex-Messclient transportiert alle Werkzeugzugänge über MCP. Der gemessene Unterschied betrifft die
<strong>Bedienung der Anwendung</strong>, nicht den Namen des Transportprotokolls.</p></section>
<section id="object-tree-evidence"><h2>Woher wissen wir, dass der Objektbaum genutzt wurde?</h2>
<p>Die gespeicherten Werkzeugaufrufe der drei erfolgreichen Computer-Use-Läufe enthalten
<code>tab.getAXState()</code>, <code>tab.setValue(22, …)</code> und <code>tab.click(23)</code>.
Die Zahlen sind beobachtete Elementreferenzen, keine Bildschirmkoordinaten. Der Messadapter zählte in jedem dieser Läufe null Bilder.
Das belegt die Bedienroute dieser Browser-Läufe; es beschreibt keine unbekannten Interna oder alle Computer-Use-Varianten.</p>
<p>Der k-lani-bot bietet bereits <code>get_ui_tree</code>, <code>find_elements</code>, <code>click_element</code> und
<code>type_text</code>. Im reinen Bildvergleich waren diese Zugänge gesperrt.
${summary.hybridAccess ? 'Die zusätzliche kombinierte Spur verwendet diese Funktionen des echten Nodes über Linux AT-SPI. '
    + 'Screenshots bleiben als Rückfall verfügbar. Chrome wurde dafür mit --force-renderer-accessibility gestartet. '
    + 'Sie verwendet weder Chrome DevTools noch DOM- oder Programmfunktionen.' : ''}</p>
<p>Ein sichtbarer Mauszeiger ist zunächst Bildinhalt. Das Objekt darunter zu ermitteln ist eine separate Abfrage:
unter Windows etwa <a href="https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation-elementfrompoint">UI Automation ElementFromPoint</a>,
unter Linux <a href="https://gnome.pages.gitlab.gnome.org/at-spi2-core/libatspi/iface.Component.html">AT-SPI GetAccessibleAtPoint</a>.
Eine solche Cursor-Abfrage wurde in diesen Computer-Use-Aufrufen nicht beobachtet. Der kombinierte Node-Test sucht direkt im Baum;
er führt keine zusätzliche Cursor-Hit-Test-Funktion ein.</p></section>
<details><summary>Alle ${summary.trials.length} Anläufe</summary><div class="table"><table><thead><tr><th>Datensatz</th><th>Verfahren</th>
<th>Anlauf</th><th>Ergebnis</th><th>Input</th><th>Gecacht</th><th>Output</th><th>Werkzeugaufrufe</th><th>Bilder</th><th>Zeit</th></tr></thead><tbody>${attempts()}</tbody></table></div></details>
<details><summary>Erstellung und Vorläufe separat</summary><p>KI-Rezept: ${number(summary.authoring.usage?.input_tokens)} Input-Tokens,
davon ${number(summary.authoring.usage?.cached_input_tokens)} gecacht; ${number(summary.authoring.usage?.output_tokens)} Output-Tokens.
Das erzeugte Rezept wurde unverändert mit einem neuen Datensatz geprüft und anschließend wiederverwendet.</p>
<p>Rezept-Erstellung im selben API-Referenztarif: $${summary.apiReferencePricing?.recipeAuthoringUsdReference?.toFixed(6) ?? 'Unbekannt'}.</p>
<p>Framework-Entwicklung und Reparatur des SDK-Namensraums wurden nicht isoliert gemessen. Deshalb gibt es hier keine vollständige
Amortisationsrechnung. Vorläufe zur Browser-Anbindung und mit unvollständiger Linux-Tastaturunterstützung gehören zur Diagnose,
ihre privaten Rohdaten bleiben erhalten. Sie ersetzen keine erfolgreichen Vergleichsaufgaben.</p></details>
<details><summary>Grenzen und Messmethode</summary><p>Das ist ein Browser-Pilot mit drei Datensätzen, keine Outlook- oder allgemeine Desktop-Abnahme.
Computer Use lief in einer eigenen Chrome-Testsitzung; k-lani-bot bediente Kiosk-Chrome in privatem Xvfb mit 1280 × 720 Pixeln.
Die Browserumgebungen unterscheiden sich.</p><p>Input, gecachter Input und Output stammen aus den vom Modellclient gemeldeten Usage-Werten.
Separate Bild- und Werkzeug-Schema-Tokens werden nicht berichtet. Gemessen wurden zusätzlich Bildanzahl, Bildbytes,
Werkzeugbeschreibung-Bytes und Ergebnis-Bytes. Bytes werden nicht in vermeintlich exakte Tokens umgerechnet.</p>
<p>Es sind keine Geldkosten: Auch gecachte Tokens können Kosten verursachen. Erfolgsraten und alle Fehlversuche bleiben sichtbar.</p>
<p>Fixture: ${escape(summary.fixture)} · lokaler Chrome: ${escape(summary.environment?.chrome)} · Quelle: ${escape(summary.createdAt)}.</p></details>
<p><a href="measurements.json">Sanitierte Messdaten als JSON</a> · <a href="measurements.csv" download>Alle Anläufe als CSV</a></p>
${summary.screenshotStudyRef ? '<p><a href="study.html">Mauszeiger, Auflösung, Palette und Bildhäufigkeit: Messreihe ansehen</a></p>' : ''}
</main><script>
const data=${chartJson};let showCached=true;const format=value=>Math.round(value).toLocaleString('de-DE');
function draw(){const key=showCached?'all':'uncached';const maximum=Math.max(...data.map(row=>row[key]));const container=document.getElementById('chart');container.replaceChildren();
for(const row of data){const item=document.createElement('div');item.className='chart-row';const label=document.createElement('div');label.className='chart-label';label.textContent=row.name;
const bar=document.createElement('div');bar.className='bar';bar.style.width=(100*row[key]/maximum)+'%';bar.style.background=row.color;
const value=document.createElement('div');value.className='chart-number';value.textContent=format(row[key])+' Tokens · '+(showCached?'Input + Output':'ungecachter Input + Output');
item.append(label,bar,value);container.append(item);}}
document.getElementById('toggle').onclick=()=>{showCached=!showCached;document.getElementById('toggle').textContent=showCached?'Gecachte Input-Tokens ausblenden':'Gecachte Input-Tokens einblenden';draw();};draw();
</script></html>`;

fs.mkdirSync(destination, { recursive: true });
fs.writeFileSync(path.join(destination, 'report.html'), html);
fs.writeFileSync(path.join(destination, 'measurements.json'), JSON.stringify(summary, null, 2));
const columns = ['route', 'case', 'attempt', 'success', 'input_tokens', 'cached_input_tokens', 'output_tokens', 'tool_calls', 'images', 'elapsed_ms'];
const csv = summary.trials.map(trial => [trial.route, trial.repetition, trial.attempt, trial.success,
    trial.usage?.input_tokens, trial.usage?.cached_input_tokens, trial.usage?.output_tokens, trial.toolCalls, trial.images, Math.round(trial.elapsedMs)].join(','));
fs.writeFileSync(path.join(destination, 'measurements.csv'), [columns.join(','), ...csv].join('\n') + '\n');
console.log(JSON.stringify({ completed, destination, trials: summary.trials.length }));
