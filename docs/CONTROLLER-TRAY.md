# Controller tray: who uses the bot, pause and switch off

The operator's computer runs one pairing master (`--pairing-master`) and one `--mcp` gateway process per MCP client
(Claude Code, Codex, another agent). The node shows who acts on it; the controller tray shows the same from the
operator's side: which nodes are registered and connected, which MCP clients use them, and lets the person pause one
client or switch the whole bot off.

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

**Commands.** The master's local administration pipe (current user, this state directory) gets four commands, usable
from the console and through `--master-admin --state DIR --command "..."`:

| Command | Effect |
| --- | --- |
| `controller-status` | Bot on/off, every registered node with connected state and version, every client row |
| `client-pause INSTANCE` | Refuse that client's calls from now on |
| `client-resume INSTANCE` | Admit them again |
| `bot on` / `bot off` | Admit or refuse every client's calls |

The status reply stays below the pipe's 16 KiB frame: at most 32 nodes and 32 clients, purposes cut to 120 characters.

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

## Boundaries

- Nodes reached directly (a registry URL pointing at `--serve`, not at the master) are neither shown as clients nor
  paused; their own node indicator and consent still apply.
- Host-side gateway work that reaches no node (catalog browse, `list_nodes`) continues while paused.
- A pause is an operator control for cooperating clients that share the controller credential, not an authentication
  boundary: a client that restarts its gateway is a new client. `bot off` covers that case.
