"""Typed contracts shared by the private desktop measurement coordinator."""
from dataclasses import dataclass, asdict
from enum import StrEnum
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import time


# The CLI selects a bounded experiment independently of ordinary correctness gates.
class Profile(StrEnum):
    SMOKE = "smoke"
    QUICK = "quick"
    MATRIX = "matrix"


# The coordinator selects installed application prerequisites and synthetic inputs.
class Suite(StrEnum):
    DESKTOP = "desktop-core"
    IMAGES = "images"
    MAIL = "mail"
    RESULT = "result-contract"
    STATE = "state-discovery"
    FORMS = "dynamic-forms"
    WORKFLOWS = "staged-workflows"
    COMPATIBILITY = "compatibility"
    RUNTIME = "native-runtime"


# A quick experiment chooses one matched source or access-route comparison.
class Comparison(StrEnum):
    BEFORE_AFTER = "before-after"
    COMPUTER_USE = "computer-use"


# The host client identifies the actual application access route.
class Route(StrEnum):
    COMPACT = "compact-node"
    WORKFLOW = "learned-mcp"
    COMPUTER = "computer-use"


# The host adapter converts received event kinds before processing them.
class EventType(StrEnum):
    PROVENANCE = "provenance"
    REQUEST = "request"
    USAGE = "usage"
    METRICS = "metrics"
    DESCRIPTIONS = "tool_descriptions"
    RESULT = "tool_result"
    ANSWER = "answer"
    COMPLETED = "completed"


# Reports distinguish verified outcomes from unavailable and incomplete evidence.
class Status(StrEnum):
    PASSED = "passed"
    FAILED = "failed"
    UNAVAILABLE = "unavailable"
    INCONCLUSIVE = "inconclusive"
    TIMEOUT = "timeout"


@dataclass(frozen=True)
# The coordinator enforces fixed profile ceilings, including per-model deadlines.
class Limits:
    seconds: int
    model_runs: int
    per_run_seconds: int


LIMITS = {
    Profile.SMOKE: Limits(120, 0, 0),
    Profile.QUICK: Limits(600, 6, 90),
    Profile.MATRIX: Limits(1200, 12, 90),
}
CLEANUP_SECONDS = 10
MAX_TRACE_BYTES = 8 * 1024 * 1024


@dataclass(frozen=True)
# Preflights retain both availability and its concrete explanation.
class Check:
    status: Status
    detail: str


@dataclass(frozen=True)
# The coordinator inspects a bounded child process result.
class ProcessResult:
    exit_code: int
    output: str


# Child operations share one monotonic deadline without extending it.
class Deadline:
    def __init__(self, seconds: float, clock=time.monotonic):
        self.clock = clock
        self.end = clock() + seconds

    def remaining(self, reserve: float = 0) -> float:
        return max(0, self.end - self.clock() - reserve)

    def require(self, reserve: float = 0) -> float:
        remaining = self.remaining(reserve)
        if remaining <= 0:
            raise TimeoutError("Global deadline reached")
        return remaining


def private_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_suffix(path.suffix + ".tmp")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w") as stream:
        json.dump(value, stream, indent=2, default=serialize)
        stream.write("\n")
    temporary.replace(path)


def serialize(value):
    if isinstance(value, Path):
        return str(value)
    return asdict(value)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def clean_environment() -> dict[str, str]:
    return {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "LANG": "C.UTF-8"}


def stop_process(process):
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait()


def execute(command: list[str], deadline: Deadline, *, cwd=None, log: Path | None = None, input_data: bytes | None = None) -> ProcessResult:
    timeout = deadline.require()
    if log:
        return execute_logged(command, deadline, cwd, log)
    with subprocess.Popen(command, cwd=cwd, env=clean_environment(), start_new_session=True,
                          stdin=subprocess.PIPE if input_data is not None else subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as process:
        try:
            output, _ = process.communicate(input_data, timeout=timeout)
        except subprocess.TimeoutExpired as exception:
            stop_process(process)
            raise TimeoutError("Process deadline reached") from exception
        except BaseException:
            stop_process(process)
            raise
    if len(output) > MAX_TRACE_BYTES:
        raise ValueError("Command output exceeded byte cap")
    return ProcessResult(process.returncode, output.decode("utf-8", errors="replace"))


def execute_logged(command: list[str], deadline: Deadline, cwd, log: Path) -> ProcessResult:
    with log.open("wb") as stream, subprocess.Popen(command, cwd=cwd, env=clean_environment(), start_new_session=True,
                                                    stdout=stream, stderr=subprocess.STDOUT) as process:
        log.chmod(0o600)
        try:
            while process.poll() is None:
                deadline.require()
                if log.stat().st_size > MAX_TRACE_BYTES:
                    raise ValueError("Private command trace exceeded byte cap")
                time.sleep(min(0.1, deadline.remaining()))
        except BaseException:
            stop_process(process)
            raise
    output = log.read_bytes()
    if len(output) > MAX_TRACE_BYTES:
        raise ValueError("Private command trace exceeded byte cap")
    return ProcessResult(process.returncode, output.decode("utf-8", errors="replace"))


def execute_to_file(command: list[str], deadline: Deadline, destination: Path, maximum_bytes: int) -> ProcessResult:
    with destination.open("wb") as stream, subprocess.Popen(command, env=clean_environment(), start_new_session=True,
                                                           stdout=stream, stderr=subprocess.DEVNULL) as process:
        destination.chmod(0o600)
        try:
            while process.poll() is None:
                deadline.require()
                if destination.stat().st_size > maximum_bytes:
                    raise ValueError("Worker output archive exceeded byte cap")
                time.sleep(min(0.1, deadline.remaining()))
        except BaseException:
            stop_process(process)
            raise
    if destination.stat().st_size > maximum_bytes:
        raise ValueError("Worker output archive exceeded byte cap")
    return ProcessResult(process.returncode, "")
