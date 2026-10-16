"""Separate frozen native runtime image, no-model real-app qualification and private lock."""
import json
from pathlib import Path
import subprocess
import tarfile
import time
import uuid

from contracts import Deadline, digest, execute, private_json, stop_process
from docker_lab import inspect_image, remove_container, wait_worker, worker_command
from runtime_mcp import RuntimeMcp, declared_text_parameter, observed_editor

PACKAGE_INVENTORY = "/usr/share/k-lani-bot-runtime-packages.tsv"
RUNTIME_NAMES = ("dotnet", "node", "nodejs", "python", "python3")


def lock_path(repository, snapshot):
    return repository / ".artifacts/performance/images" / snapshot.worker_digest / "native-runtime.json"


def prepare_runtime(repository, snapshot, deadline, output):
    context = output / "build-context"
    context.mkdir(mode=0o700)
    with tarfile.open(snapshot.archive) as stream:
        stream.extractall(context, filter="data")
    recipe = context / "docker/runtime/Dockerfile"
    if not recipe.is_file():
        raise ValueError("Frozen source has no native runtime target; do not substitute a different source recipe")
    tag = "k-lani-bot-runtime:" + snapshot.digest[:16]
    command = ["docker", "build", "--platform", "linux/amd64", "--target", "runtime", "-f", str(recipe), "-t", tag, str(context)]
    built = execute(command, deadline, log=output / "build.log")
    if built.exit_code:
        raise ValueError("Native runtime build failed; inspect private build.log")
    image = inspect_image(tag, deadline)
    qualification = qualify(image["Id"], Deadline(min(120, deadline.require(10))), output)
    packages = execute(["docker", "run", "--rm", "--pull=never", "--network=none", "--entrypoint", "cat",
                        image["Id"], PACKAGE_INVENTORY], deadline)
    if packages.exit_code:
        raise ValueError("Native runtime package inventory is unavailable")
    lock = {"status": qualification["status"], "target": "native-runtime", "source_digest": snapshot.digest,
            "worker_source_digest": snapshot.worker_digest, "image_digest": image["Id"], "repo_digests": image["RepoDigests"],
            "dependency_lock": json.loads((context / "docker/runtime/dependencies.lock.json").read_text()),
            "capabilities": {"fingerprints": {"packages_sha256": digest(packages.output.encode()),
                                             "mousepad_version": qualification.get("application_build")}},
            "qualification": qualification, "packages": packages.output.splitlines()}
    private_json(lock_path(repository, snapshot), lock)
    return lock


def accepted_runtime(repository, snapshot, deadline):
    file = lock_path(repository, snapshot)
    if not file.is_file():
        raise ValueError("No separate prepared native runtime lock for frozen source")
    lock = json.loads(file.read_text())
    if (lock.get("target") != "native-runtime" or lock.get("worker_source_digest") != snapshot.worker_digest
            or lock.get("status") != "passed" or lock.get("qualification", {}).get("status") != "passed"
            or set(lock.get("qualification", {}).get("absent_runtimes", [])) != set(RUNTIME_NAMES)
            or any(lock.get("qualification", {}).get(field) is not True for field in
                   ("independent_saved_file", "controller_restart", "form_scan", "verified_first_use", "verified_reuse"))):
        raise ValueError("Native runtime lock has not passed actual app/runtime qualification")
    image = inspect_image(lock["image_digest"], deadline)
    if image["Id"] != lock["image_digest"]:
        raise ValueError("Native runtime image fingerprint changed")
    return {**lock, "build_source_digest": lock["source_digest"], "source_digest": snapshot.digest}


def worker_execute(worker, arguments, deadline, *, session=True):
    prefix = ["docker", "exec", worker]
    if session:
        prefix.append("/app/session-exec.sh")
    result = execute(prefix + arguments, deadline)
    if result.exit_code:
        raise ValueError("Native runtime worker command failed: " + arguments[0])
    return result.output


def initialize_worker(worker, deadline, output):
    worker_execute(worker, ["/app/k-lani-bot-node", "--init", "--backup-requirement", "off", "--node", "runtime-lab", "--state", "/tmp/node-state",
                            "--workspace", "/workspace", "--bind", "127.0.0.1", "--port", "5443",
                            "--scopes", "observe,ui,clipboard", "--ui-guard", "desktop",
                            "--application", "/usr/bin/mousepad", "--consent-mode", "unattended", "--ui-language", "en"], deadline)
    worker_execute(worker, ["mkdir", "-p", "/tmp/cartridges"], deadline)
    command = ["docker", "exec", worker, "/app/session-exec.sh", "/app/k-lani-bot-node", "--serve", "--config",
               "/tmp/node-state/node.json"]
    with (output / "node-supervisor.log").open("wb") as log:
        return subprocess.Popen(command, stdout=log, stderr=log, start_new_session=True)


def absent_runtimes(worker, deadline):
    observed = []
    for runtime in RUNTIME_NAMES:
        result = execute(["docker", "exec", worker, "sh", "-c", 'command -v "$1"', "runtime-probe", runtime], deadline)
        if result.exit_code not in (1, 127) or result.output.strip():
            raise ValueError("Additional bot runtime present or absence probe failed: " + runtime)
        observed.append(runtime)
    return observed


def save_and_verify(client, worker, process, expected, deadline):
    client.call("ui_act", {"pid": process, "steps": [{"action": "key", "keys": ["ctrl", "s"]}], "result_mode": "summary"})
    for _ in range(50):
        actual = worker_execute(worker, ["cat", "/workspace/document.txt"], deadline, session=False)
        if actual == expected:
            return
        time.sleep(min(0.1, deadline.require()))
    raise ValueError("Independent application-saved file did not exactly match the requested full text")


def round_trip(worker, deadline):
    first = "Native first use: Größe / café\n"
    second = "Native restarted reuse: exact replacement\n"
    for _ in range(50):
        ready = execute(["docker", "exec", worker, "sh", "-c", "test -f /tmp/node-state/nodes.json"], deadline)
        if ready.exit_code == 0:
            break
        time.sleep(min(0.1, deadline.require()))
    with RuntimeMcp(worker, deadline) as client:
        window, field, scope = observed_editor(client, deadline)
        scan = client.call("cartridge_call", {"function": "Info.scan_form", "arguments": {
            "pid": window["pid"], "form": "editor", "result_mode": "summary"}})
        if scan.get("success") is not True:
            raise ValueError("Actual native form scan failed")
        written = client.call("ui_act", {"pid": window["pid"], "steps": [{"action": "write", "ref": field["ref"], "text": first}],
                                         "result_mode": "summary"})
        definitions = [item for item in written.get("mcp", {}).get("functions", []) if item.get("verified") is True]
        if len(definitions) != 1:
            raise ValueError("First-use native write produced no one verified reusable definition")
        name = definitions[0]["function"]
        save_and_verify(client, worker, window["pid"], first, deadline)
    # A killed controller never releases its desktop session, so the restart would be refused for the lease time.
    if client.exit_code != 0:
        raise ValueError("First controller did not exit cleanly, so its desktop session was not released")
    with RuntimeMcp(worker, deadline) as restarted:
        window, _, _ = observed_editor(restarted, deadline)
        browsed = restarted.call("cartridge_browse", {"function": name})
        parameter = declared_text_parameter(browsed)
        restarted.call("cartridge_call", {"function": name, "arguments": {
            "pid": window["pid"], "parameters": {parameter: second}, "result_mode": "summary"}})
        save_and_verify(restarted, worker, window["pid"], second, deadline)
    return {"application_build": window["version"], "independent_saved_file": True, "controller_restart": True,
            "form_scan": True, "verified_first_use": True, "verified_reuse": True}


def qualify(image, deadline, output):
    name = "k-lani-native-runtime-" + uuid.uuid4().hex
    owned = []
    report = {"status": "unavailable", "absent_runtimes": [], "independent_saved_file": False}
    command = worker_command(image, name)
    command.insert(2, "--detach")
    command.extend(["sleep", "infinity"])
    try:
        if execute(command, deadline).exit_code:
            raise ValueError("Private native runtime worker failed to start")
        wait_worker(name, deadline)
        runtimes = absent_runtimes(name, deadline)
        report["absent_runtimes"] = runtimes
        # This is initial fixture input, never an application output or a hidden answer supplied to the controller.
        worker_execute(name, ["sh", "-c", "printf 'Initial private fixture input\\n' > /workspace/document.txt"], deadline)
        owned.append(initialize_worker(name, deadline, output))
        app = ["docker", "exec", name, "/app/session-exec.sh", "mousepad", "--disable-server", "/workspace/document.txt"]
        owned.append(subprocess.Popen(app, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True))
        report = {"status": "passed", "absent_runtimes": runtimes, **round_trip(name, deadline)}
        private_json(output / "runtime-qualification.json", report)
        return report
    except (ValueError, OSError, TimeoutError) as error:
        report["detail"] = str(error)
        private_json(output / "runtime-qualification.json", report)
        raise
    finally:
        for process in reversed(owned):
            stop_process(process)
        remove_container(name, Deadline(10))
