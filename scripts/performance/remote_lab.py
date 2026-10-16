"""Operate an isolated lab node through the existing authenticated node/MCP transport."""
import argparse
from enum import StrEnum
import json
import os
from pathlib import Path
import signal
import socket
import shutil
import subprocess
import sys
import time
import uuid

from contracts import Deadline, ProcessResult, clean_environment, execute, private_json, stop_process
from docker_lab import accepted_image
from snapshot import freeze

REPOSITORY = Path(__file__).resolve().parents[2]
DEFAULT_DIRECTORY = REPOSITORY / ".artifacts/performance/remote-lab"
NODE_NAME = "performance-lab"
STARTUP_SECONDS = 20
CALL_SECONDS = 65


# The operator selects one lifecycle or transport action without starting another model.
class LabCommand(StrEnum):
    START = "start"
    STOP = "stop"
    STATUS = "status"
    CALL = "call"


def private_directory(path: Path) -> Path:
    directory = path.resolve()
    artifact_root = (REPOSITORY / ".artifacts").resolve()
    if directory == artifact_root or not directory.is_relative_to(artifact_root):
        raise ValueError("Lab state must stay inside the ignored artifact directory")
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    directory.chmod(0o700)
    return directory


def connection(directory: Path) -> dict:
    return json.loads((directory / "connection.json").read_text())


def owns_supervisor(record: dict, directory: Path) -> bool:
    process_id = record.get("pid")
    binary = record.get("binary")
    if type(process_id) is not int or process_id <= 0 or not isinstance(binary, str):
        return False
    path = Path("/proc") / str(process_id) / "cmdline"
    if not path.is_file():
        return False
    try:
        arguments = path.read_bytes().decode().split("\0")
    except (OSError, UnicodeError):
        return False
    return arguments[:3] == [record["binary"], "--serve", "--config"] and str(directory / "node-state/node.json") in arguments


def available_port() -> int:
    with socket.socket() as channel:
        channel.bind(("127.0.0.1", 0))
        return channel.getsockname()[1]


def initialize(directory: Path, binary: Path, image: str, deadline: Deadline):
    workspace = directory / "workspace"
    workspace.mkdir(exist_ok=True, mode=0o700)
    resources = {"Pids": 256, "MemoryMiB": 2048, "Cpus": 2, "SharedMemoryMiB": 128, "TemporaryMiB": 768}
    private_json(directory / "resources.json", resources)
    command = [str(binary), "--init", "--backup-requirement", "off", "--node", NODE_NAME, "--state", str(directory / "node-state"),
               "--workspace", str(workspace), "--bind", "127.0.0.1", "--port", str(available_port()),
               "--scopes", "observe,ui,clipboard,process,files.read,files.write",
               "--application", "/usr/bin/mousepad", "--application", "/app/k-lani-bot-cartridge-lab",
               "--sandbox-image", image, "--sandbox-resources", str(directory / "resources.json"),
               "--consent-mode", "unattended", "--ui-guard", "desktop", "--ui-language", "en"]
    result = execute(command, deadline, log=directory / "initialize.log")
    if result.exit_code:
        raise ValueError("Private node initialization failed; inspect initialize.log")


def bind_worker_image(directory: Path, image: str):
    path = directory / "node-state/node.json"
    configuration = json.loads(path.read_text())
    worker = configuration["worker"]
    if worker[:2] != ["docker", "run"]:
        raise ValueError("Refusing to change an unrelated worker configuration")
    images = [index for index, value in enumerate(worker) if value.startswith("sha256:")]
    if len(images) != 1:
        raise ValueError("Private worker requires one pinned image")
    worker[images[0]] = image
    private_json(path, configuration)


def start(directory: Path, binary: Path) -> dict:
    existing = directory / "connection.json"
    if existing.exists() and owns_supervisor(connection(directory), directory):
        raise ValueError("This lab is already running; stop it before changing its image")
    if not binary.is_file():
        raise ValueError("Build the node executable with scripts/build.sh --check first")
    deadline = Deadline(STARTUP_SECONDS)
    snapshot = freeze(REPOSITORY, directory / "source-snapshot", deadline=deadline)
    lock = accepted_image(REPOSITORY, snapshot, deadline)
    if not (directory / "node-state/node.json").exists():
        initialize(directory, binary, lock["image_digest"], deadline)
    bind_worker_image(directory, lock["image_digest"])
    with (directory / "node.log").open("ab") as stream:
        process = subprocess.Popen([str(binary), "--serve", "--config", str(directory / "node-state/node.json")],
                                   stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
                                   start_new_session=True, env=clean_environment())
    record = {"pid": process.pid, "binary": str(binary), "node": NODE_NAME, "imageDigest": lock["image_digest"],
              "registry": str(directory / "node-state/nodes.json"), "sourceDigest": snapshot.digest}
    private_json(directory / "connection.json", record)
    configure_gateway(directory, binary, record)
    try:
        return wait_ready(record, directory, deadline)
    except BaseException:
        stop(directory)
        raise


def configure_gateway(directory: Path, binary: Path, record: dict):
    private_json(directory / "gateway.json", {"command": str(binary), "arguments": [
        "--mcp", "--nodes", record["registry"], "--default-node", NODE_NAME]})
    node = shutil.which("node")
    if node is None:
        return
    private_json(directory / "compact-meter.json", {
        "command": node,
        "arguments": [str(REPOSITORY / "modules/desktop-ui-mcp/server.mjs"), str(directory / "gateway.json")],
        "route": "compact-node", "tools": ["ui_info", "ui_observe", "ui_act"],
        "metricsFile": str(directory / "mcp-metrics.json")})


def call(record: dict, request: dict, deadline: Deadline, directory: Path) -> dict:
    if not isinstance(request, dict) or set(request) != {"action", "args"} or not isinstance(request["args"], dict):
        raise ValueError("Supply one node request with action and args")
    command = [record["binary"], "--call", "--nodes", record["registry"], "--node", record["node"], "--h2",
               "--client-name", "Codex desktop", "--task", "Private remote lab operation"]
    result = exchange(command, request, deadline, directory)
    lines = result.output.splitlines()
    if result.exit_code or len(lines) != 2:
        raise ValueError("Node transport returned no confirmed response and session cleanup")
    response = json.loads(lines[0])
    cleanup = json.loads(lines[1])
    if response.get("success") is True and cleanup.get("success") is not True:
        raise ValueError("The transient node session could not be closed")
    private_json(directory / "last-response.json", response)
    return response


def new_operation_id() -> str:
    # RFC 9562:2024 permits a millisecond timestamp followed by random payload bits.
    identifier = list(str(uuid.uuid4()))
    timestamp = format(time.time_ns() // 1_000_000, "012x")
    identifier[:8] = timestamp[:8]
    identifier[9:13] = timestamp[8:]
    identifier[14] = "7"
    return "".join(identifier)


def exchange(command: list[str], request: dict, deadline: Deadline, directory: Path) -> ProcessResult:
    with (directory / "call-errors.log").open("ab") as errors, subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors,
            start_new_session=True, env=clean_environment()) as process:
        try:
            cleanup = {"action": "end_session", "args": {"request_id": new_operation_id()}}
            frames = json.dumps(request) + "\n" + json.dumps(cleanup) + "\n"
            output, _ = process.communicate(frames.encode(), timeout=deadline.require())
        except subprocess.TimeoutExpired as error:
            stop_process(process)
            raise TimeoutError("Remote node call deadline reached") from error
    if len(output) > 4 * 1024 * 1024:
        raise ValueError("Remote node result exceeds its transport bound")
    return ProcessResult(process.returncode, output.decode("utf-8"))


def wait_ready(record: dict, directory: Path, deadline: Deadline) -> dict:
    while deadline.remaining() > 0:
        if not owns_supervisor(record, directory):
            raise ValueError("Private node exited during startup; inspect node.log")
        response = call(record, {"action": "get_machine_capabilities", "args": {}}, deadline, directory)
        if response.get("success") is True:
            return {"success": True, "node": record["node"], "imageDigest": record["imageDigest"],
                    "platform": response["capabilities"]["platform"], "gateway": str(directory / "gateway.json")}
        time.sleep(min(0.2, deadline.require()))
    raise TimeoutError("Private node readiness deadline reached")


def stop(directory: Path) -> dict:
    record = connection(directory)
    if not owns_supervisor(record, directory):
        return {"success": True, "stopped": True, "alreadyStopped": True}
    os.kill(record["pid"], signal.SIGTERM)
    deadline = Deadline(5)
    while owns_supervisor(record, directory) and deadline.remaining() > 0:
        time.sleep(0.1)
    if owns_supervisor(record, directory):
        os.killpg(record["pid"], signal.SIGKILL)
    return {"success": True, "stopped": True}


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description="Remote private lab through bot-node; no model process or provider credentials.")
    parser.add_argument("command", type=LabCommand, choices=list(LabCommand))
    parser.add_argument("--directory", type=Path, default=DEFAULT_DIRECTORY)
    parser.add_argument("--binary", type=Path, default=REPOSITORY / ".artifacts/check-build/linux/bin/k-lani-bot-node/debug/k-lani-bot-node")
    arguments = parser.parse_args()
    try:
        directory = private_directory(arguments.directory)
        if arguments.command == LabCommand.START:
            result = start(directory, arguments.binary.resolve())
        elif arguments.command == LabCommand.STOP:
            result = stop(directory)
        elif arguments.command == LabCommand.STATUS:
            result = {"success": True, "running": owns_supervisor(connection(directory), directory)}
        else:
            result = call(connection(directory), json.load(sys.stdin), Deadline(CALL_SECONDS), directory)
        print(json.dumps(result))
        return 0 if result.get("success") is True else 3
    except (OSError, ValueError, KeyError, TimeoutError) as error:
        print(json.dumps({"success": False, "error": str(error)}))
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
