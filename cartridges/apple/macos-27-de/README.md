# Erste Apple-Cartridge: macOS 27 auf Deutsch

`apple-macos-27-de-install` installiert **k-lani-bot-node.app**, setzt die
Dateirechte, provisioniert den Node und registriert seinen Login-Dienst. Die
Cartridge ist für macOS 27 / de-DE geprüft. Sie ist keine iOS-Cartridge.

Der Inhalt von `cartridge.json` beschreibt einen fest implementierten,
versionierten Installationsablauf. Er enthält keine auszuführenden Shellskripte,
Download-URLs oder modelgenerierten Root-Befehle. Die CLI ist ein vertrauenswürdiger
Admin-/Operator-Einstieg; die Cartridge wird nicht als beliebiger Root-MCP-Tool
für fremde Agenten freigeschaltet. Alle Operationen erhalten UUIDv7-IDs.

## Einmal planen, dann ausführen

Auf dem Mac mit dem veröffentlichten Node und einem geprüften App-Bundle:

```sh
scripts/install-macos.sh /path/k-lani-bot-node.app
```

Dieser eine Aufruf übernimmt Planung und Hash-Übergabe automatisch. Für getrennte
Prüfung und Ausführung stehen die gleichen CLI-Phasen bereit:

```sh
k-lani-bot-node --cartridge cartridge.json --cartridge-action plan --source-bundle /path/k-lani-bot-node.app
k-lani-bot-node --cartridge cartridge.json --cartridge-action apply --source-bundle /path/k-lani-bot-node.app --source-sha256 <source_sha256-aus-plan>
```

Für eine vorhandene stabile Signatur kann `--signing-identity <40-stelliger-
Zertifikat-Fingerprint>` ergänzt werden. Der private Schlüssel wird nicht
exportiert oder gelesen. Verlangt macOS eine Schlüsselbundfreigabe, muss sie im
lokalen OS-Dialog erfolgen; keine Passwörter in MCP, Logs, Parametern oder Chat.
Der Installer kann auch als App über `open -n -a <source-bundle> --args ...`
gestartet werden, damit diese normalen Apple-Dialoge im GUI-Kontext erscheinen.
Ad-hoc-Signaturen sind für lokale Tests möglich, aber kein Ersatz für eine
stabile, passend signierte Distribution.

Standardpfade:

- App: `/Applications/k-lani-bot-node.app` (Ordner „Programme“; Vorgängerversion nach einem Update unter
  `~/Library/Application Support/k-lani-bot-node/previous-bundles`)
- Privater State: `~/Library/Application Support/k-lani-bot-node/state`
- Agent-Workspace: `~/Library/Application Support/k-lani-bot-node/workspace`
- Login-Dienst: `~/Library/LaunchAgents/k-lani-bot-node.plist`

`--install-root`, `--data-root` und `--port` erlauben explizite andere Ziele. Der
Node bindet zunächst nur 127.0.0.1 und erhält observe/ui/files/macro-Fähigkeiten;
es gibt keinen dauerhaften Root-Daemon, keine globale sudoers-Freigabe und keine
stillschweigende Shell-Vollmacht. Eine Installation in ein nicht beschreibbares
Systemverzeichnis scheitert, statt heimlich zu eskalieren. Allgemeine privilegierte
PKG-Installation braucht einen separaten, eng begrenzten OS-Autorisierungsweg.

## Was der Ablauf prüft

1. macOS-Version, Sprache, MDM-Zustand und Installationspfade.
2. Bundle-ID, Entry Point, Code-Signatur und der zuvor festgelegte Quell-Hash.
3. Kopie in ein frisches Staging-Verzeichnis, erneute Integritätsprüfung und
   atomaren Austausch. Vorherige App-Versionen werden als Backup erhalten.
4. Privaten State: Verzeichnisse 0700, Dateien 0600. Der Workspace bleibt getrennt.
5. Node-Konfiguration und bestehende Credentials. Wiederholungen ersetzen keine
   gültigen Schlüssel und übernehmen keinen fremden oder veränderten Dienst.
6. Eigenen launchd-Login-Agent im Kontext des angemeldeten Benutzers.
7. Datenschutzanfrage aus der tatsächlich installierten App-Identität.
8. Rechte über den authentifizierten, tatsächlichen GUI-Worker, nicht über einen
   SSH-Prozess mit anderen TCC-Rechten. Fehlende Freigaben ergeben
   `awaiting_os_authorization`, nicht einen falschen Erfolg.

## Apple-Datenschutz ist ein eigener Berechtigungsbereich

Auf dem geprüften macOS 27 (26A428) heißen die Bereiche:

- **Gerätesteuerung und Datenzugriff**: Accessibility.
- **Aufnahme von Bildschirm & Systemaudio**: Screen Capture.

Der laufende GUI-Dienst öffnet per unterstützter OS-Navigation den zuerst
benötigten Datenschutzbereich und liefert einen korrelierten Rechtebericht.
Der Ablauf schreibt nicht in TCC-Datenbanken, deaktiviert SIP nicht
und gibt sich keine Rechte durch manipulierte Profile. Eine erste Freigabe auf
einem unverwalteten Mac ist nicht vollständig lautlos automatisierbar.
Die Berechtigungsnavigation wird über eine private Operator-Datei außerhalb des
Agent-Workspace an den laufenden GUI-Dienst übergeben. Dadurch gehört der
Rechtebericht zur tatsächlich installierten App-Identität. Native Prompt-APIs
sind als gesonderte GUI-Diagnose `--request-apple-permissions` verfügbar; der
dauerhafte Console-Login-Agent verwendet sie nicht, weil ein wartender OS-Dialog
seine Rechteprüfungen blockieren kann.

Zusätzlich entsteht `k-lani-bot-node-pppc.mobileconfig` mit der tatsächlichen
Designated Code Requirement der installierten App. Nur ein entsprechend
verwaltetes Gerät kann diesen PPPC-Payload über MDM anwenden. Accessibility wird
mit `Allow` beschrieben; Screen Capture ausdrücklich mit
`AllowStandardUserToSetSystemService`, **nicht** mit einem erfundenen Silent-Allow.
Das erzeugte Profil wird nicht automatisch installiert oder als angewendet gemeldet.

[Apple PPPC-Payload](https://support.apple.com/en-au/guide/deployment/dep38df53c2a/web),
[Apple Authorization-Werte](https://developer.apple.com/documentation/devicemanagement/privacypreferencespolicycontrol/services-data.dictionary/identity).

## Wiederaufnahme und Prüfung

```sh
k-lani-bot-node --cartridge cartridge.json --cartridge-action permissions
k-lani-bot-node --cartridge cartridge.json --cartridge-action verify --verify-timeout-seconds 60
k-lani-bot-node --cartridge cartridge.json --cartridge-action stop
```

Exit-Code 0 bedeutet den benannten erfolgreichen Zustand (`planned`, `verified`
oder `stopped`), 3 wartet auf Apple-Freigabe und 2 meldet einen Fehler. Der
private `last-run.json` enthält den tatsächlichen Status, UUIDv7 und Phasen.
Der erste `plan` erzeugt keine Installationsdateien. `--no-permission-prompts`
erlaubt einen Installations-/Betriebstest ohne weitere OS-Prompts. Root wird für
GUI-Setup abgelehnt; der Dienst soll als angemeldeter Nutzer laufen.
