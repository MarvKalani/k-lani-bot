# Controller tray: who uses the bot, pause and switch off

The operator's computer runs one pairing master (`--pairing-master`) and one `--mcp` gateway process per MCP client
(Claude Code, Codex, another agent). The node shows who acts on it; the controller tray shows the same from the
operator's side: which nodes are registered and connected, how each node is doing, which MCP clients use them, and lets
the person pause one client or switch the whole bot off.

## Design

**The master observes and enforces.** A gateway reaches a paired node only through the master's
`/node/{node}/action` and `/node/{node}/session` routes. Every such request already carries the session header and the
presentation header (`clientInfo.name` and `K_LANI_BOT_TASK`), and an active session sends a heartbeat every 20 seconds.
The master therefore keeps the live client table itself and refuses calls of a paused client or of a switched-off bot.
No new channel between gateway and master is needed, and older gateways are covered too.

**One row per gateway process.** A gateway holds one transport client, and therefore one session, per node. Each
transport client also sends `X-K-Lani-Bot-Instance`, one random identifier per process, so the master groups the
sessions of one gateway into one client row with the nodes it used. A gateway without the header (an older build)
appears once per session. Name and purpose are the client's own claim; pauses are keyed by the instance identifier,
never by the name, and the tray says the name is as reported.

**Refusal.** A refused action is answered with HTTP 200 and `success: false`, `outcome: not_dispatched`,
`code: access_paused` and a plain message (paused in the tray, or bot switched off). The gateway returns a failed tool
result with that full JSON as text, which is the message the AI sees. While paused, three requests still pass:
`get_machine_capabilities` (read-only; keeps `tools/list` complete for a client that started while paused, since the
gateway does not announce list changes), `end_session` (releases the desktop lease at once instead of after five minutes)
and heartbeat/close presence events. When a client is paused, the master tells each node the client used that its
session closed, so the node's indicator no longer shows it; heartbeats of a paused client are counted but not forwarded.

**State.** Client rows and per-client pauses live in the master's memory: a row ends with the gateway's close event or
after 60 seconds without any request (a gateway that called `end_session`, as every gateway does when it exits, stops its
heartbeats). A paused row stays until its gateway's close or 12 hours of silence, so a pause does not lapse while the
client waits. A restarted gateway is a new, unpaused client; to
stop every client, including new ones, switch the bot off. The on/off switch is persisted privately as
`controller-access.json` in the master's state directory, so a restarted master stays off.

**Node vitals.** A node that sends `X-K-Lani-Bot-Vitals: 1` when it connects answers the master's `vitals` request
with its operating system, available and total memory, free and total space of its system volume (`C:\`, `/`, or
`/System/Volumes/Data` on macOS), CPU load and uptime since the system started (sleep included). The master asks right
after the node connects and then every `poll_seconds` (default 30), but only while the node's relay is idle, so a poll
never delays an action. The node answers from memory and never waits: CPU load is the average since the previous request, so the first
answer has none. A value the node cannot measure is `null`, never an estimate. The master shortens the node's text,
strips control characters and drops numbers out of range before the tray shows them or a model reads them. A node
without the header (an older build) is never asked, because an unknown request would end its connection.

Last contact is the node's last answer to any relayed call, or its connection. It lives in the master's memory: offline
nodes keep it but lose their vitals, and after a master restart a node that has not reconnected shows "no contact since
the master started". The tray shows two indented lines under a connected node, for example
`Windows 11 (Build 22621), läuft seit 3 T 4 h` and `CPU 12 % · RAM 3,1 von 8,0 GB frei · C:\ 41 von 237 GB frei`, plus
the last contact under every node. Contact under a minute reads "gerade eben" and sizes of 10 GB or more lose their
decimal, but load and memory still change, so the menu text changes about every poll. The Windows tray therefore
replaces an open menu only after it closes. The Linux tray hands the new menu to the indicator at once; whether a panel
keeps an open menu then is not yet checked on a real desktop.

`list_nodes` asks the master's authenticated `GET /node/{node}/status` for every node in parallel, with a three-second
limit. The route answers from the master's cache: it reaches no node, holds no desktop lease and does not make the
caller a client in the tray. A model sees vitals only when they matter: each entry carries `status` (`connected`,
`offline`, or `unknown` when no master answered in time, for example for a node registered directly) and, only when a
limit is crossed, `alerts` such as `disk_low: C:\ 3.0 of 237 GB free`. The operator sees every value in the tray, where
the load line of a node with an alert is marked with ⚠. The unauthenticated `/node/{node}/health` route stays without
vitals.

**Settings and history.** `vitals.json` in the master's state directory sets the limits, how often the master asks and
how often and how long it keeps each node's vitals. The master reads it each time it reports or polls, so a change applies
without a restart. A missing name keeps its default; an invalid file is reported on the master's console and the
defaults apply, so a typo never silences every alert.

```json
{
  "memory_free_percent": 10,
  "disk_free_percent": 5,
  "disk_free_gb": 5,
  "cpu_load_percent": 90,
  "poll_seconds": 30,
  "history_interval_seconds": 600,
  "history_days": 7
}
```

The values shown are the defaults. Memory is low below `memory_free_percent` of the total; the disk is low below
`disk_free_percent` of its size or below `disk_free_gb`, so small and large disks both warn in time; the processor is
busy above `cpu_load_percent` on average since the previous poll. `poll_seconds` (10 to 3600) is how often each node
measures; a slower poll spares the nodes. `history_interval_seconds` (10 to 86400, never faster than the poll) is how
often a sample is kept, for example 60, 600 or 3600; `history_days` (0 to 366, 0 keeps none) is how long. Samples go
to `vitals-history/NODE.jsonl` in the state directory, private to the master's user, one JSON line per sample with time,
vitals and alerts; older samples are removed about once an hour. A node removed from the master keeps its file until the
operator deletes it.

`vitals-history NODE [HOURS]` (default 24) answers with the period's sample count, the highest CPU load and the least
free memory and disk, each with its time, how often each limit was crossed and the path of the full file. AI clients get
no history.

**Commands.** The master's local administration pipe (current user, this state directory) gets five commands, usable
from the console and through `--master-admin --state DIR --command "..."`:

| Command | Effect |
| --- | --- |
| `controller-status` | Bot on/off, every registered node with connected state, version, vitals and seconds since last contact, every client row |
| `client-pause INSTANCE` | Refuse that client's calls from now on |
| `client-resume INSTANCE` | Admit them again |
| `bot on` / `bot off` | Admit or refuse every client's calls |
| `vitals-history NODE [HOURS]` | Peaks and crossed limits of a node's vitals in the last hours (default 24) |

The status reply stays below the pipe's 16 KiB frame: node rows take at most 8 KiB (about 18 nodes with vitals), client
rows the rest, purposes cut to 120 characters, and entries beyond the budget are counted as "more".

**Tray process.** `k-lani-bot-node --controller-tray --state MASTER_STATE_DIR` is its own process. It never derives from
or creates the node's visibility host (no border, no consent window). It asks the master for `controller-status` every
two seconds off the UI thread and rebuilds its menu when the answer changed. Without a running master it shows that and
lists the nodes of `nodes.json` with unknown state. The menu is computed by a platform-neutral model
(`ControllerTrayMenu`) that the tests check headless; the native hosts only draw it.

| Platform | Tray | Status |
| --- | --- | --- |
| Linux | StatusNotifierItem through `libayatana-appindicator3` (KDE, GNOME with AppIndicator extension, Wayland and X11) | Implemented; menu model tested headless, tray process runs in the private test display (no panel check on a real desktop yet) |
| Windows | `WindowsNativeTray` (Shell_NotifyIcon) with a WPF context menu | Implemented and cross-built; native acceptance open |
| macOS | — | Refused with a clear message; the menu model is ready for an NSStatusItem host |

| Platform | Vitals source | Status |
| --- | --- | --- |
| Linux | `/etc/os-release`, `/proc/meminfo` (MemAvailable), `/proc/stat`, `/proc/uptime` | Parsers unit-tested; measured end to end by the real node in the pairing regression |
| Windows | `GlobalMemoryStatusEx`, `GetSystemTimes`, `GetTickCount64`, build number for Windows 10 or 11 | Cross-built and name rule unit-tested; not yet run on a Windows node |
| macOS | `hw.memsize` with `kern.memorystatus_level`, `host_statistics` CPU ticks, `kern.boottime` | Cross-built; not yet run on a Mac |

## Boundaries

- How the refusal reads depends on the gateway route. Core tools and routed node functions (`backup_*`, `app_mcp_*`) return
  the master's JSON as a failed tool result (covered end to end). A stored cartridge recipe reports it as a JSON-RPC error
  "The node refused the window check (access_paused): …", and `cartridge_select`/`cartridge_info` return the refusal itself;
  both are unit-checked, not end to end. The compact profile (`--ui-profile compact`) is not yet checked.
- Gateways started before this change send no instance header; they show one row per node session and can be paused the
  same way. Only the master and the tray need the new build.

- Nodes reached directly (a registry URL pointing at `--serve`, not at the master) are neither shown as clients nor
  paused; their own node indicator and consent still apply.
- Host-side gateway work that reaches no node (catalog browse, `list_nodes`) continues while paused.
- A pause is an operator control for cooperating clients that share the controller credential, not an authentication
  boundary: a client that restarts its gateway is a new client. `bot off` covers that case.
