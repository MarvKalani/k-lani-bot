"""Host-only MCP transport for the self-contained native controller in a private runtime worker."""
import json
import selectors
import subprocess
import time

from contracts import Deadline, clean_environment, stop_process


# Each test session owns one headless native controller; the operated worker alone owns GUI visibility.
class RuntimeMcp:
    MAXIMUM_FRAME_BYTES = 1024 * 1024
    # The controller releases its desktop session after stdin ends (up to 5 s); a kill earlier would skip the
    # release and lock the restarted controller out until the node's lease expires.
    CLEAN_EXIT_SECONDS = 8

    def __init__(self, worker: str, deadline: Deadline):
        command = ["docker", "exec", "-i", worker, "env", "-u", "DISPLAY", "-u", "WAYLAND_DISPLAY",
                   "-u", "DBUS_SESSION_BUS_ADDRESS", "-u", "XDG_RUNTIME_DIR", "-u", "GDK_BACKEND",
                   "/app/k-lani-bot-node", "--mcp", "--ui-profile", "compact", "--nodes", "/tmp/node-state/nodes.json",
                   "--default-node", "runtime-lab", "--cartridges", "/tmp/cartridges", "--learning-root", "/tmp/learning"]
        self.deadline = deadline
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, env=clean_environment(), start_new_session=True)
        self.sequence = 0
        self.buffer = bytearray()
        self.exit_code = None
        try:
            self.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                       "clientInfo": {"name": "native-runtime-acceptance", "version": "1"}})
            self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        except BaseException:
            self.close()
            raise

    def send(self, frame):
        if self.process.poll() is not None:
            raise ValueError("Native MCP controller exited")
        self.process.stdin.write((json.dumps(frame, separators=(",", ":")) + "\n").encode())
        self.process.stdin.flush()

    def request(self, method, parameters):
        self.sequence += 1
        self.send({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": parameters})
        while True:
            frame = self.read_frame()
            if frame.get("id") == self.sequence:
                if "error" in frame:
                    raise ValueError("Native MCP request failed: " + str(frame["error"].get("message")))
                return frame["result"]

    def read_frame(self):
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdout, selectors.EVENT_READ)
            while b"\n" not in self.buffer:
                if not selector.select(self.deadline.require()):
                    raise TimeoutError("Native MCP response deadline reached")
                chunk = self.process.stdout.read1(65536)
                if not chunk:
                    raise ValueError("Native MCP output ended before its response")
                self.buffer.extend(chunk)
                if len(self.buffer) > self.MAXIMUM_FRAME_BYTES:
                    raise ValueError("Native MCP response exceeds frame bound")
        line, _, rest = self.buffer.partition(b"\n")
        self.buffer = bytearray(rest)
        return json.loads(line)

    def call(self, name, arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        value = result.get("structuredContent")
        if not isinstance(value, dict):
            raise ValueError("Native MCP call returned no structured result")
        if value.get("success") is False:
            raise ValueError("Native MCP operation refused: " + str(value.get("error")))
        return value

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=min(self.CLEAN_EXIT_SECONDS, max(0.1, self.deadline.remaining())))
            except subprocess.TimeoutExpired:
                stop_process(self.process)
        self.exit_code = self.process.poll()
        self.process.stdout.close()

    def __enter__(self):
        return self

    def __exit__(self, kind, value, traceback):
        self.close()


OBSERVED_PAGE_LIMIT = 200
MAXIMUM_OBSERVED_PAGES = 5


def observed_elements(client: RuntimeMcp, pid: int):
    """Reads the provider-visible view completely; a view cut off by the page budget is never evaluated.

    The full view of Mousepad starts with hundreds of off-screen menu items, so a single default page
    never reaches the editor field. Visibility is requested from the provider instead of guessed from names."""
    page = client.call("ui_observe", {"pid": pid, "full": True, "offscreen": False, "limit": OBSERVED_PAGE_LIMIT})
    elements = list(page.get("elements", []))
    pages = 1
    while page.get("truncated") and page.get("next_cursor") and pages < MAXIMUM_OBSERVED_PAGES:
        page = client.call("ui_observe", {"pid": pid, "cursor": page["next_cursor"], "limit": OBSERVED_PAGE_LIMIT})
        elements.extend(page.get("elements", []))
        pages += 1
    if page.get("truncated"):
        raise ValueError("Observed application view exceeds the bounded page budget")
    return elements


def declared_text_parameter(browsed):
    """Returns the one text parameter the browsed generated function declares; the name comes from its schema."""
    schema = browsed.get("function", {}).get("inputSchema", {})
    properties = schema.get("properties", {}).get("parameters", {}).get("properties", {})
    names = [name for name, declaration in properties.items() if declaration.get("type") == "string"]
    if len(names) != 1 or len(properties) != 1:
        raise ValueError("Generated function must declare exactly one text parameter")
    return names[0]


def select_editor(windows, elements):
    """Returns the single installed Mousepad window with its one editor field and owning scope, else None."""
    mousepad = [window for window in windows if window.get("process_name") == "mousepad"]
    if len(mousepad) != 1 or mousepad[0].get("version") in (None, "unknown"):
        return None
    fields = [element for element in elements if element.get("role") == "Edit"]
    scopes = [element for element in elements if element.get("role") in ("Window", "frame")]
    if len(fields) != 1 or len(scopes) != 1:
        return None
    return mousepad[0], fields[0], scopes[0]


def observed_editor(client: RuntimeMcp, deadline: Deadline):
    for _ in range(50):
        try:
            info = client.call("ui_info", {})
            windows = info.get("windows", [])
            mousepad = [window for window in windows if window.get("process_name") == "mousepad"]
            elements = observed_elements(client, mousepad[0]["pid"]) if len(mousepad) == 1 else []
        except ValueError as error:
            if "Transport failed (connection_failed)" not in str(error):
                raise
            time.sleep(min(0.1, deadline.require()))
            continue
        selected = select_editor(windows, elements)
        if selected:
            return selected
        time.sleep(min(0.1, deadline.require()))
    raise ValueError("One current installed Mousepad editor and owning scope are required")
