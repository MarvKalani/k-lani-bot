"""Freeze only source files; runtime state and verifier answers never enter worker mounts."""
from dataclasses import dataclass
import io
import json
from pathlib import Path, PurePosixPath
import tarfile
from contracts import Deadline, digest, execute

SOURCE_ROOTS = {"src", "contracts", "assets", "tools", "modules", "docker", "scripts"}
SOURCE_FILES = {"Directory.Build.props", "global.json"}
SOURCE_SUFFIXES = {".cs", ".csproj", ".props", ".targets", ".json", ".mjs", ".py", ".sh", ".png", ".svg", ".html", ".css", ".js", ".resx", ".xml", ".xaml"}
PRIVATE_PARTS = {"bin", "obj", "__pycache__", "node_modules", "credentials.json", "policy.json", "nodes.json"}


@dataclass(frozen=True)
# The freezer captures a named source file with its bytes and executable mode.
class SourceFile:
    path: str
    content: bytes
    mode: int


@dataclass(frozen=True)
# A measurement binds the source archive and worker build inputs to immutable fingerprints.
class Snapshot:
    digest: str
    archive: Path
    files: list[dict]
    worker_digest: str


def permitted(name: str) -> bool:
    path = PurePosixPath(name)
    if not path.parts:
        return False
    if path.is_absolute() or any(part.startswith(".") or part in PRIVATE_PARTS for part in path.parts):
        return False
    if name in SOURCE_FILES or name == "src/k-lani-bot-node/Actions/Inspector.html.txt" or (path.parts[0] == "docker" and path.name == "Dockerfile"):
        return True
    if name == "scripts/performance/oracle.py" or "golden" in path.parts:
        return False
    return path.parts[0] in SOURCE_ROOTS and path.suffix in SOURCE_SUFFIXES


def freeze(repository: Path, destination: Path, reference: str = "working-tree", deadline: Deadline | None = None) -> Snapshot:
    destination.mkdir(parents=True, exist_ok=True, mode=0o700)
    operation_deadline = deadline or Deadline(15)
    if reference == "working-tree":
        names = execute(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                        operation_deadline, cwd=repository).output.split("\0")
        entries = read_working_tree(repository, names)
    else:
        resolved = execute(["git", "rev-parse", "--verify", reference + "^{commit}"], operation_deadline, cwd=repository)
        if resolved.exit_code:
            raise ValueError("Baseline is not an available commit")
        entries = read_commit(repository, resolved.output.strip(), operation_deadline)
    manifest = [{"path": entry.path, "sha256": digest(entry.content), "mode": entry.mode} for entry in entries]
    fingerprint = digest(json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode())
    archive = destination / (fingerprint + ".tar")
    with tarfile.open(archive, "w") as stream:
        for entry in entries:
            item = tarfile.TarInfo(entry.path)
            item.size = len(entry.content)
            item.mode = entry.mode
            stream.addfile(item, io.BytesIO(entry.content))
    archive.chmod(0o400)
    worker_manifest = [entry for entry in manifest if worker_source(entry["path"])]
    worker_digest = digest(json.dumps(worker_manifest, sort_keys=True, separators=(",", ":")).encode())
    return Snapshot(fingerprint, archive, manifest, worker_digest)


def read_working_tree(repository, names):
    entries = []
    for name in sorted(set(names)):
        if not name or not permitted(name):
            continue
        path = repository / name
        if path.is_symlink():
            raise ValueError("Source snapshot rejects symbolic links: " + name)
        if path.is_file():
            mode = 0o755 if path.stat().st_mode & 0o111 else 0o644
            entries.append(SourceFile(name, path.read_bytes(), mode))
    return entries


def read_commit(repository, reference, deadline):
    result = subprocess_archive(repository, reference, deadline)
    entries = []
    with tarfile.open(fileobj=io.BytesIO(result)) as stream:
        for item in sorted(stream.getmembers(), key=lambda entry: entry.name):
            if permitted(item.name) and not item.isdir():
                if not item.isfile():
                    raise ValueError("Committed snapshot rejects symbolic links")
                entries.append(SourceFile(item.name, stream.extractfile(item).read(), item.mode))
    return entries


def subprocess_archive(repository, reference, deadline):
    import subprocess
    from contracts import clean_environment, stop_process
    with subprocess.Popen(["git", "archive", reference], cwd=repository, env=clean_environment(),
                          start_new_session=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as process:
        try:
            output, _ = process.communicate(timeout=deadline.require())
        except subprocess.TimeoutExpired as exception:
            stop_process(process)
            raise TimeoutError("Committed source snapshot deadline reached") from exception
        if process.returncode:
            raise ValueError("Committed source snapshot could not be read")
    return output


def worker_source(name: str) -> bool:
    prefixes = ("src/", "contracts/", "assets/branding/", "tools/CodeStandardAnalyzer/", "tools/k-lani-bot-cartridge-lab/")
    runtime = {"docker/performance/Dockerfile", "docker/performance/entrypoint.sh", "docker/performance/session-exec.sh",
               "docker/performance/capabilities.py", "docker/performance/dependencies.lock.json"}
    return name in SOURCE_FILES or name in runtime or name.startswith(prefixes) or name.startswith("docker/runtime/")


def materialize_tool_source(snapshot: Snapshot, destination: Path) -> Path:
    destination.mkdir(mode=0o700)
    with tarfile.open(snapshot.archive) as archive:
        selected = []
        for member in archive.getmembers():
            path = PurePosixPath(member.name)
            gateway = member.name.startswith("modules/desktop-ui-mcp/") and not path.name.endswith(".test.mjs")
            if gateway or member.name == "contracts/tools.json":
                selected.append(member)
        archive.extractall(destination, members=selected, filter="data")
    for path in destination.rglob("*"):
        path.chmod(0o500 if path.is_dir() else 0o400)
    destination.chmod(0o500)
    return destination
