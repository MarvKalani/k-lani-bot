"""Docker lifecycle with isolated disposable profiles and a separately accepted image lock."""
from dataclasses import dataclass
import json
from pathlib import Path
import tarfile
import uuid
from contracts import Check, Deadline, Status, execute, execute_to_file, private_json
from snapshot import Snapshot


@dataclass(frozen=True)
# The coordinator owns one disposable container and its immutable image.
class Worker:
    name: str
    image: str


def worker_command(image: str, name: str, inputs: Path | None = None, *, network="none") -> list[str]:
    command = ["docker", "run", "--pull=never", "--name", name, "--network", network,
               "--user", "10001:10001", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges",
               "--pids-limit", "256", "--memory", "2g", "--cpus", "2", "--ipc=private",
               "--tmpfs", "/tmp:rw,nosuid,nodev,size=768m,uid=10001,gid=10001",
               "--tmpfs", "/workspace:rw,nosuid,nodev,size=128m,uid=10001,gid=10001"]
    if inputs:
        command.extend(["--mount", f"type=bind,src={inputs.resolve()},dst=/inputs,readonly"])
    command.append(image)
    return command


def image_lock_path(repository: Path, snapshot: Snapshot, mail=False) -> Path:
    return repository / ".artifacts/performance/images" / snapshot.worker_digest / ("mail.json" if mail else "desktop.json")


def infrastructure(deadline: Deadline) -> Check:
    try:
        result = execute(["docker", "info", "--format", "{{.ServerVersion}}"], deadline)
    except (OSError, TimeoutError):
        return Check(Status.UNAVAILABLE, "Docker executable or daemon unavailable")
    return Check(Status.PASSED if result.exit_code == 0 else Status.UNAVAILABLE,
                 "Docker daemon reachable" if result.exit_code == 0 else "Docker daemon unavailable")


def inspect_image(image: str, deadline: Deadline) -> dict:
    result = execute(["docker", "image", "inspect", image], deadline)
    if result.exit_code:
        raise ValueError("Prepared image is absent; scored runs never build or pull")
    return json.loads(result.output)[0]


def probe(image: str, deadline: Deadline, output: Path) -> dict:
    name = "k-lani-perf-probe-" + uuid.uuid4().hex
    try:
        startup = worker_command(image, name)
        startup.insert(2, "--detach")
        startup.extend(["sleep", "infinity"])
        started = execute(startup, deadline)
        if started.exit_code:
            raise ValueError("Capability worker failed to start")
        wait_worker(name, deadline)
        command = ["docker", "exec", name, "/app/session-exec.sh", "python3", "/app/capabilities.py"]
        result = execute(command, Deadline(min(100, deadline.require(10))), log=output / "capabilities.log")
        lines = [line for line in result.output.splitlines() if line.startswith('{"fingerprints"')]
        if not lines:
            raise ValueError("Application capability probe returned no report")
        report = json.loads(lines[-1])
        outputs = output / "capability-outputs"
        export_directory(name, "/tmp/capabilities", outputs, deadline)
        from oracle import verify_capabilities
        verdict = verify_capabilities(outputs)
        report["independent_verification"] = verdict
        required = {"webp", "zip", "text_pdf", "spreadsheet"}
        accepted = (result.exit_code == 0 and verdict.status == Status.PASSED
                    and set(report["checks"]) == required and all(check["status"] == "passed" for check in report["checks"].values()))
        report["status"] = "passed" if accepted else "unavailable"
        return report
    finally:
        remove_container(name, deadline)


def remove_container(name: str, deadline: Deadline):
    if deadline.remaining() > 0:
        execute(["docker", "rm", "-f", name], Deadline(min(5, deadline.remaining())))


def prepare(repository: Path, snapshot: Snapshot, deadline: Deadline, output: Path, *, mail=False, greenmail_image=None) -> dict:
    context = output / "build-context"
    context.mkdir(mode=0o700)
    with tarfile.open(snapshot.archive) as stream:
        stream.extractall(context, filter="data")
    tag = "k-lani-bot-performance:" + snapshot.digest[:16] + ("-mail" if mail else "-desktop")
    command = ["docker", "build", "--platform", "linux/amd64", "--target", "mail" if mail else "desktop",
               "-f", str(context / "docker/performance/Dockerfile"), "-t", tag, str(context)]
    result = execute(command, Deadline(deadline.require(10)), log=output / "build.log")
    if result.exit_code:
        return {"status": "unavailable", "detail": "Image build failed; inspect private build.log"}
    image = inspect_image(tag, deadline)
    capabilities = probe(image["Id"], deadline, output)
    packages = execute(["docker", "run", "--rm", "--pull=never", "--network=none", "--read-only",
                        "--entrypoint", "cat", image["Id"], "/usr/share/k-lani-bot-performance-packages.tsv"],
                       deadline).output
    (output / "packages.tsv").write_text(packages)
    lock = {"status": capabilities["status"], "source_digest": snapshot.digest, "worker_source_digest": snapshot.worker_digest, "image_digest": image["Id"],
            "repo_digests": image["RepoDigests"], "dependency_lock": json.loads(
                (repository / "docker/performance/dependencies.lock.json").read_text()),
            "capabilities": capabilities, "packages": packages.splitlines()}
    if mail:
        if not greenmail_image:
            lock["status"] = "unavailable"
            lock["mail_service"] = {"status": "unavailable", "detail": "Explicit pinned GreenMail image digest is required"}
        else:
            from mail_service import transport_probe
            pulled = execute(["docker", "pull", greenmail_image], deadline, log=output / "greenmail-pull.log")
            if pulled.exit_code:
                raise ValueError("Pinned GreenMail image could not be prepared")
            lock["mail_service"] = transport_probe(image["Id"], greenmail_image, deadline)
    private_json(image_lock_path(repository, snapshot, mail), lock)
    return lock


def accepted_image(repository: Path, snapshot: Snapshot, deadline: Deadline, mail=False) -> dict:
    path = image_lock_path(repository, snapshot, mail)
    if not path.is_file():
        raise ValueError("No prepared image lock for the frozen source snapshot")
    lock = json.loads(path.read_text())
    required = {"webp", "zip", "text_pdf", "spreadsheet"}
    capabilities = lock.get("capabilities", {})
    checks = capabilities.get("checks", {})
    if (lock["worker_source_digest"] != snapshot.worker_digest or lock["status"] != "passed"
            or set(checks) != required or any(check.get("status") != "passed" for check in checks.values())
            or capabilities.get("independent_verification", {}).get("status") != "passed"):
        raise ValueError("Prepared image has not passed application capability acceptance")
    image = inspect_image(lock["image_digest"], deadline)
    if image["Id"] != lock["image_digest"]:
        raise ValueError("Prepared image fingerprint changed")
    return {**lock, "build_source_digest": lock["source_digest"], "source_digest": snapshot.digest}


def start_worker(lock: dict, inputs: Path, deadline: Deadline) -> Worker:
    name = "k-lani-perf-trial-" + uuid.uuid4().hex
    command = worker_command(lock["image_digest"], name, inputs)
    command.insert(2, "--detach")
    command.extend(["sleep", "infinity"])
    try:
        result = execute(command, deadline)
        if result.exit_code:
            raise ValueError("Isolated worker failed to start")
        wait_worker(name, deadline)
        created = execute(["docker", "exec", name, "mkdir", "-p", "/tmp/cartridges"], deadline)
        if created.exit_code:
            raise ValueError("Worker private cartridge storage is unavailable")
    except BaseException:
        remove_container(name, deadline)
        raise
    return Worker(name, lock["image_digest"])


def copy_outputs(worker: Worker, output: Path, deadline: Deadline):
    export_directory(worker.name, "/workspace", output, deadline)


def export_directory(worker: str, source: str, output: Path, deadline: Deadline):
    maximum_bytes = 16 * 1024 * 1024
    output.mkdir(mode=0o700)
    archive = output.with_suffix(".tar")
    command = ["docker", "exec", worker, "tar", "-cf", "-", "-C", source, "."]
    result = execute_to_file(command, deadline, archive, maximum_bytes)
    if result.exit_code:
        raise ValueError("Could not retrieve actual worker output")
    with tarfile.open(archive) as stream:
        total_bytes = 0
        for item in stream.getmembers():
            if not item.isfile() and not item.isdir():
                raise ValueError("Worker output rejects links and special files")
            total_bytes += item.size
            if total_bytes > maximum_bytes:
                raise ValueError("Expanded worker output exceeds byte cap")
        stream.extractall(output, filter="data")


def wait_worker(name: str, deadline: Deadline):
    import time
    for attempt in range(50):
        result = execute(["docker", "exec", name, "/app/session-exec.sh", "xdotool", "getdisplaygeometry"], deadline)
        if result.exit_code == 0:
            return
        time.sleep(min(0.1, deadline.require()))
    raise ValueError("Worker private session is unavailable")
