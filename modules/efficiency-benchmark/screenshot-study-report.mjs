import fs from 'node:fs';
import path from 'node:path';
import { summarizeScreenshotStudy } from './screenshot-study-summary.mjs';

const report = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const summary = report.format === 'k-lani-bot-screenshot-study' ? report : summarizeScreenshotStudy(report);
const output = path.resolve(process.argv[3]);
const labels = { 'pixel-original': 'Original · RGB', 'pixel-cursor': 'RGB + Mauszeiger', 'pixel-half': 'Halbe Auflösung · RGB',
    'pixel-palette': 'Maximal 256 Farben', 'pixel-sparse': 'Weniger Bildaufrufe · RGB' };
const value = number => number === null || number === undefined ? 'Unbekannt' : Math.round(number).toLocaleString('de-DE');
const seconds = number => number === null ? 'Unbekannt' : (number / 1000).toFixed(2) + ' s';
const imageCount = number => number === null ? 'Unbekannt' : number.toLocaleString('de-DE', { maximumFractionDigits: 1 });
const rows = summary.groups.map(group => `<tr><td>${labels[group.variant]}</td><td>${group.successes}/${group.attempts}</td>`
    + `<td>${group.imageDimensions.join(', ')}</td><td>${group.pngColorTypes.join(', ')}</td>`
    + `<td>${value(group.medianImageBytes)}</td><td>${imageCount(group.medianImagesOnSuccess)}</td>`
    + `<td>${seconds(group.medianElapsedMsOnSuccess)}</td><td>${seconds(group.medianModelDecisionMsBeforeClick)}</td>`
    + `<td>${seconds(group.medianBackendClickMs)}</td></tr>`).join('');

const html = `<!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>k-lani-bot · Screenshot-Varianten</title><style>
:root{color-scheme:dark;background:#101b2b;color:#edf5ff;font:16px/1.6 system-ui,sans-serif}body{margin:0}
main{max-width:1350px;margin:auto;padding:32px 24px}h1{font-size:32px;line-height:1.2}p{max-width:1100px}
section{background:#1a2b42;border:1px solid #536781;border-radius:14px;padding:20px;margin:24px 0}
.table{overflow:auto}table{border-collapse:collapse;white-space:nowrap;width:100%}th,td{padding:12px;text-align:right;border-bottom:1px solid #536781}
th:first-child,td:first-child{text-align:left}.accent,a{color:#78dfc9}details{margin:20px 0}summary{cursor:pointer}
</style><main><p class="accent">Explorativer Pilot · ${summary.model} · ${summary.reasoningEffort}</p>
<h1>Mauszeiger, Bildgröße, Farben und Aufnahmehäufigkeit</h1>
<p>Jede Variante bekommt dieselben drei Rechner-/Editor-/Datei-Aufgaben in einer frischen privaten Testsitzung.
Alle Ergebnisse werden unabhängig anhand der tatsächlichen Dateien geprüft. Eine Variante mit weniger Bildern gilt erst bei
erfolgreicher Ausführung als Verbesserung.</p>
<section><h2>Messwerte</h2><div class="table"><table><thead><tr><th>Variante</th><th>Erfolg</th><th>Bildgröße</th><th>PNG-Farbtyp</th>
<th>Bildbytes · Median</th><th>Bilder bei Erfolg · Median</th><th>Aufgabe bei Erfolg · Median</th>
<th>Wartezeit vor Klick · Median</th><th>Klick-Backend · Median</th></tr></thead><tbody>${rows}</tbody></table></div>
<p>PNG-Farbtyp 2 bedeutet RGB; 3 bedeutet indizierte Palette. Bildbytes zählen die tatsächliche Werkzeugausgabe.
Wartezeit vor Klick umfasst Modellverarbeitung und Transport seit der vorigen Werkzeugantwort, nicht ausschließlich Inferenz.
Das Klick-Backend misst die Ausführung nach Eingang des Befehls.</p></section>
<section><h2>Was sich damit feststellen lässt</h2><p>Unsere ursprüngliche Werkzeugausgabe enthält 1280 × 720 RGB-Pixel.
Die Halbierungsvariante liefert tatsächlich 640 × 360 Pixel und rechnet Zeigerkoordinaten zurück. Die Palette ist auf höchstens
256 Farben begrenzt. Damit sind Änderungen an der Werkzeuggrenze beobachtbar.</p>
<p>Interne Vorverarbeitung des Modellanbieters bleibt unbekannt. Eine kleinere PNG-Datei beweist keine geringere Zahl an Bild-Tokens.
Die <a href="https://developers.openai.com/api/docs/guides/images-vision">OpenAI-Dokumentation</a> beschreibt von Modell,
Bildgröße und Detailstufe abhängige Verarbeitung.</p></section>
<section><h2>Grenzen</h2><p>Je drei Datensätze sind eine kleine Stichprobe. Die Cursorpositionen wurden in diesem Pilot nicht vor jedem Lauf
zurückgesetzt; ein kausaler Geschwindigkeitsvorteil des Mauszeigers ist dadurch nicht bewiesen. Wartezeiten hängen zusätzlich von
Modell, Transport, Cache und Ablaufstrategie ab.</p><p>Zeitüberschreitungen ohne Usage-Bericht bleiben bei Tokens und Dollarwerten
unbekannt. Sie werden weder als kostenlos noch als erfolgreiche Aufgaben gewertet.</p></section>
<p><a href="study.json">Bereinigte Messdaten als JSON</a></p></main></html>`;

fs.mkdirSync(output, { recursive: true });
fs.writeFileSync(path.join(output, 'study.html'), html);
fs.writeFileSync(path.join(output, 'study.json'), JSON.stringify(summary, null, 2));
console.log(JSON.stringify({ output, variants: summary.groups.length, attempts: summary.trials.length }));
