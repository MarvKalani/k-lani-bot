# Get started with k-lani-bot

Connect your AI client to **k-lani-bot**, then operate the registered **k-lani-node** components on the target computers.
These instructions build the developer alpha from source. The local example below uses the compact UI profile with one
node; [multiple-node operation](AGENT-OPERATIONS.md#2-connect-to-the-correct-node) uses the existing core or native cartridge route.
Use a signed-in, disposable test desktop session. A source build does not establish native platform acceptance.
The node's private state must stay outside the workspace it can operate on.

Private, non-commercial use is free under the Additional Use Grant. Non-production evaluation is also permitted for
businesses; business production needs a separately acquired license. Read [the license model](legal/LICENSING.md).
Before state-changing use, qualify the actual application/workflow and verify backups and recovery for its affected data.
Use the [operator review record](legal/OPERATOR-REVIEW.md) to document that scope; a successful build or AI review alone
does not establish that an arbitrary application operation is safe.

Coding agents: first read [Agent operations](AGENT-OPERATIONS.md) to reuse existing access, select the correct gateway
and distinguish node setup from model login and performance measurement.

## Components and executable

The MCP server is **k-lani-bot**. It loads saved actions and reaches registered nodes; its routing code is called a gateway.
Each **k-lani-node** operates the applications on its target computer and applies local access policy.
The controller's pairing service accepts approved nodes that connect back to it. It belongs to the same product.

Both roles currently use the same C# executable, named `k-lani-bot-node` (`.exe` on Windows).
`--mcp` starts the AI-facing server; `--serve` or `--connect` starts the operated node's connection process.
Build each executable for the operating system where it runs. A Windows controller and Windows nodes need no Linux host.
The same applies to macOS and Linux: **both the MCP/controller and node roles are implemented and built for all three
desktop platforms**. A controller may reach nodes on different operating systems. The quickstart's Windows/Linux command
examples do not limit the controller to those platforms; macOS app packaging is linked in the build section below.
The intended fleet may mix Linux, macOS, Windows, Android and iOS nodes behind that one MCP, regardless of the controller's
desktop operating system. Current Android and iOS bridges have the limits in the platform table; independent mobile nodes
are not established by this architecture statement.

The planned Git publication contains source and build instructions. The existing Linux executable package is a private
candidate retained for testing, not a selected download. MCP and node source for Windows, macOS and Linux remains included.
Docker supplies isolated test/optional worker environments; it is not required for normal native desktop operation.

## Runtime and build requirements

The self-contained `k-lani-bot-node` build includes its .NET runtime. End users running that executable do not need
the .NET SDK or a separate .NET installation. The SDK is required only on the machine that builds the executable.
If you already have a binary built for your platform, use its absolute path and continue with the node setup below.

The core gateway and the native `--mcp --ui-profile compact` frontend are part of the executable and require no Node.js runtime.
The native profile supports observation, verified input, forms, parameterized capture/reuse and receipt-bound compatibility
assessment/revalidation and private staged workflows. A configured private learning
root enables automatic first-use capture; see [Agent operations](AGENT-OPERATIONS.md) for the existing-node start command.
The JavaScript development/parity reference in `modules/desktop-ui-mcp` runs under Node.js 22+.
Minimal-runtime image and complete application acceptance remain separate from a successful source build.
Platform helpers still apply, such as the X11 tools on Linux, ADB for Android and the XCTest runner for iOS Simulator.

## Build

Download or clone this repository, then open a terminal in its root.
Install the .NET SDK matching [global.json](../global.json). The JavaScript reference gateway and development parity tests
also need Node.js 22+; that implementation uses built-in Node.js modules and needs no npm install.

**Windows x64 — PowerShell:**

```powershell
dotnet publish src/k-lani-bot-node/k-lani-bot-node.csproj -c Release -f net10.0-windows -r win-x64 -p:TargetFrameworks=net10.0-windows --self-contained true -o dist
```

**Linux x64 — Bash:**

```bash
dotnet publish src/k-lani-bot-node/k-lani-bot-node.csproj -c Release -f net10.0 -r linux-x64 -p:TargetFrameworks=net10.0 --self-contained true -o dist
```

The measured Linux route uses X11 and AT-SPI. On Ubuntu 24.04, the desktop helpers used by the node are:

```bash
sudo apt-get install at-spi2-core libatspi2.0-0 libgtk-3-0t64 dbus-x11 xdotool xclip scrot wmctrl
```

Native macOS code is also present, with separate app-bundle and permission setup.
The existing [macOS installation cartridge](../cartridges/apple/macos-27-de/README.md) is documented in German and targets macOS 27;
it is outside this Windows/Linux quickstart. Browser benchmark results do not establish support for every application or platform.

## Start the node

Initialize once. This creates a private registry, credentials and policy. It refuses to overwrite existing state.
The following configuration allows desktop observation, UI input and clipboard access, with operator consent.

**Windows — PowerShell:**

```powershell
$botExecutable = (Resolve-Path .\dist\k-lani-bot-node.exe).Path
$botState = Join-Path $env:LOCALAPPDATA 'k-lani-bot\nodes\local'
$botWorkspace = Join-Path $env:USERPROFILE 'k-lani-bot-work'
& $botExecutable --init --node local --state $botState --workspace $botWorkspace --bind 127.0.0.1 --port 5443 --scopes observe,ui,clipboard --ui-guard desktop --consent-mode ask --ui-language en
& $botExecutable --serve --config (Join-Path $botState 'node.json')
```

**Linux — Bash:**

```bash
bot_executable="$PWD/dist/k-lani-bot-node"
bot_state="$HOME/.local/share/k-lani-bot/local"
bot_workspace="$HOME/k-lani-bot-work"
"$bot_executable" --init --node local --state "$bot_state" --workspace "$bot_workspace" --bind 127.0.0.1 --port 5443 --scopes observe,ui,clipboard --ui-guard desktop --consent-mode ask --ui-language en
"$bot_executable" --serve --config "$bot_state/node.json"
```

Keep the serving process running. To restart, run only the `--serve` command.
Keep the executable at the initialized location. Keep the state directory and its credentials private and outside Git.
This example binds to the same computer; configuring a remote node is a separate setup.

New nodes require a current backup before the first change of each session; until backup is configured, changes are
refused with `backup_required`. On a disposable test machine the administrator can create the node with
`--backup-requirement off` instead. See [backup setup](ADMINISTRATION-FOUNDATION.md#node-tools-and-operator-setup).

## Connect your MCP client

Register **k-lani-bot** using the executable's native compact MCP profile. Create a cartridge library and a separate private learning directory
on the MCP host, outside the workspace the node operates. For a new local Linux setup:

```bash
install -d -m 700 "$HOME/.local/share/k-lani-bot/cartridges" "$HOME/.local/share/k-lani-bot/learning"
```

On Windows, create separate `cartridges` and `learning` directories under your private local application-data directory,
with permissions restricted to the operator. The library must exist before gateway startup. Start with an empty library
or your own reviewed cartridge definitions; pointing at a download folder is not cartridge acceptance.

Register this stdio server in your MCP client. Replace all placeholders with absolute paths; Windows paths can use forward
slashes and the command ends in `k-lani-bot-node.exe`. Clients using an `mcpServers` configuration can use:

```json
{
  "mcpServers": {
    "k-lani-bot": {
      "command": "/absolute/path/to/dist/k-lani-bot-node",
      "args": [
        "--mcp", "--ui-profile", "compact",
        "--nodes", "/absolute/private/state/nodes.json",
        "--default-node", "local",
        "--cartridges", "/absolute/private/cartridges",
        "--learning-root", "/absolute/private/learning"
      ]
    }
  }
}
```

This example selects one default node for compact UI. The registry is the `nodes.json` created by initialization, or the existing authorized registry. Keep its referenced
credentials private. This starts a gateway using existing access; it does not initialize or replace pairing state.
Client configuration formats vary; use the same command and arguments in your client's server settings. No Node.js process
or second MCP server is needed for this route.
Call `ui_info`, choose the intended application. Run a stored function that it lists with `cartridge_call`, or otherwise
use `ui_observe` and `ui_act`.
The gateway returns compact observations and changes. The node continues to enforce access and input policy.

## Enable automatic function capture

The native configuration above already supplies `--learning-root`. Omit that option when new functions must not be saved;
observation and use of existing functions remain available. Keep the learning directory separate from the cartridge library.
Start with `ui_info` to identify the actual application/build, select its observed process and use the returned semantic
references. An unknown build cannot be learned or treated as compatible. Do not enter a guessed version to make a check pass.

Successful text writes and verified tab navigation can now install a generated function during `ui_act`, without naming it in the reply.
Find it with `cartridge_browse` on topic `Use/UI`; `cartridge_call` runs it with new parameter values. `ui_info` lists named functions
with their parameter names under their window rows.
Other actions remain unverified candidates until a suitable result check is established.
Function discovery does not require restarting the gateway after each capture.

Automatic capture covers supported, verified actions; the agent still authors the complete business procedure.
Continue with [Mail → Tryton](AGENT-AUTHORING.md#an-executable-example-make-a-whole-tryton-order-one-call) to build, expose
and reuse one complete draft-order action. The example uses existing products, not product-master creation.

The core MCP gateway and JavaScript parity frontend remain available with different configuration contracts. Their
[route-specific setup](AGENT-OPERATIONS.md#2-connect-to-the-correct-node) is for developers who need those routes;
do not mix the JavaScript `learning` JSON with the native CLI options shown here.

## If setup stops

- **State already exists:** preserve it and start using its existing `node.json`.
- **Connection fails:** check the serving process, registry path and port; both processes must use the same private state.
- **No desktop controls appear:** check the signed-in session and native accessibility support. The Linux pilot uses X11.
- **Input is refused:** check the node's visible consent controls and the selected target; do not replay uncertain writes.
- **Learning is unavailable:** check `--learning-root`, directory permissions and the observed application/build.

## Platform backends

The node selects a native desktop backend or an explicitly configured mobile target.
Use `get_machine_capabilities` through the core MCP gateway to check which tools the current target exposes.
An implemented backend does not imply identical capabilities or benchmark coverage across platforms.

| Target | Access route and limits |
| --- | --- |
| Windows | Native UI Automation. Use an interactive desktop session and compatible application controls. |
| macOS | Native Accessibility. App packaging and OS permissions have a separate setup. |
| Linux | AT-SPI reads and semantic actions can work on X11 or Wayland when available. Screenshots, clipboard, raw input and window management currently require X11. |
| Android | ADB and UIAutomator for an authorized device or emulator. Input is foreground tapping/typing; no semantic background input, clipboard route or reliable modal detection. |
| iOS Simulator | Experimental XCTest bridge on a macOS host with a running test runner for one fixed app. No physical-device or general iPhone/iPad automation claim. |
| Browsers | Applications controlled through the host backend. The measured fixture uses Chrome; other browser/OS combinations need their own checks. |

Mobile routes use the core node gateway, not the desktop compact gateway shown above.
Android selection uses `--android --serial <device-serial>` and optionally `--adb <executable>` during initialization.
iOS selection uses `--ios-bridge <private-bridge-directory> --ios-bundle <bundle-id>` with the XCTest runner already started.
These are advanced backend selections, not mobile installers or a claim that automatic desktop capture works on mobile.

Implementation references: [backend selection](../src/k-lani-bot-node/Startup/ActuatorCommands.cs),
[Android](../src/k-lani-bot-node/Platforms/Mobile/AdbUiService.cs),
[iOS Simulator](../src/k-lani-bot-node/Platforms/Mobile/IosSimulatorUiService.cs),
[XCTest bridge](../tests/Apple/k-lani-bot-ios-bridge/Bridge.swift),
and [Linux](../src/k-lani-bot-node/Platforms/Linux/LinuxUiService.cs).

[Back to README](../README.md) · [Cartridge design and scope](DESKTOP-AUTOMATION.md)
