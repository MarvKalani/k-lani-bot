"""One host-side model client drives both routes through a bounded JSON-lines adapter."""
from dataclasses import dataclass
from decimal import Decimal
import json
import os
from pathlib import Path
import selectors
import subprocess
import time
import uuid
from contracts import EventType, MAX_TRACE_BYTES, Deadline, Route, Status, clean_environment, stop_process
from telemetry import Ledger, validate_provenance


@dataclass(frozen=True)
# A reviewed host client uses explicit private credentials, capabilities and budgets.
class ProviderConfiguration:
    command: list[str]
    client_version: str
    credentials: list[str]
    supported: list[dict]
    routes: dict
    max_tokens: int
    max_calls: int
    max_cost: Decimal
    currency: str
    matrix: list[dict]


@dataclass(frozen=True)
# The event consumer retains the observed client outcome and final read observation.
class AdapterState:
    status: Status
    provenance: dict
    answer: str | None


@dataclass(frozen=True)
# The coordinator combines client evidence with independent application verification.
class AdapterResult:
    status: Status
    exit_code: int | None
    totals: dict
    provenance: dict
    elapsed_seconds: float
    answer: str | None


def configuration(path: Path) -> ProviderConfiguration:
    if not path.is_file() or path.is_symlink() or path.stat().st_mode & 0o077:
        raise ValueError("Provider configuration must be a private regular file (mode 600)")
    value = json.loads(path.read_text())
    command = value["command"]
    if not isinstance(command, list) or not command or not all(isinstance(part, str) and part for part in command):
        raise ValueError("Adapter command must be an argument array")
    if not Path(command[0]).is_absolute() or not Path(command[0]).is_file():
        raise ValueError("Adapter executable must be an existing absolute path")
    budget = value["budget"]
    for name in ("max_tokens", "max_calls"):
        if type(budget[name]) is not int or budget[name] <= 0:
            raise ValueError("Explicit positive token and call budgets are required")
    cost = Decimal(str(budget["max_reference_cost"]))
    if not cost.is_finite() or cost <= 0 or not value["client_version"] or not budget["currency"]:
        raise ValueError("Explicit reference-cost budget, currency and client version are required")
    credentials = value["credential_environment"]
    if not isinstance(credentials, list) or not credentials or any(not os.environ.get(name) for name in credentials):
        raise ValueError("Authorized provider credentials are unavailable")
    forbidden = {"HOME", "DISPLAY", "WAYLAND_DISPLAY", "DOCKER_HOST", "PATH", "DBUS_SESSION_BUS_ADDRESS"}
    if any(name in forbidden for name in credentials):
        raise ValueError("Provider environment must contain credential variables only")
    return ProviderConfiguration(command, value["client_version"], credentials, value["supported"], value["routes"],
                                 budget["max_tokens"], budget["max_calls"], cost, budget["currency"], value.get("matrix", []))


def provider_environment(config: ProviderConfiguration, home: Path) -> dict:
    environment = clean_environment()
    environment["HOME"] = str(home)
    for name in config.credentials:
        environment[name] = os.environ[name]
    return environment


def check_combination(config: ProviderConfiguration, model: str, reasoning: str, route: Route):
    combination = {"model": model, "reasoning": reasoning, "route": route.value}
    if combination not in config.supported:
        raise ValueError("Unsupported model, reasoning or route combination")
    validate_provenance(route, config.routes.get(route.value, {}))


def availability(config, model, reasoning, route, deadline, output):
    check_combination(config, model, reasoning, route)
    home = output / ("preflight-home-" + route.value + "-" + uuid.uuid4().hex)
    home.mkdir(mode=0o700)
    request = {"mode": "preflight", "model": model, "reasoning": reasoning, "route": route.value,
               "paid_calls_allowed": False}
    with subprocess.Popen(config.command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                          start_new_session=True, cwd=home, env=provider_environment(config, home)) as process:
        try:
            response, _ = process.communicate(json.dumps(request).encode() + b"\n", timeout=min(15, deadline.require()))
        except subprocess.TimeoutExpired as exception:
            stop_process(process)
            raise TimeoutError("Provider preflight deadline reached") from exception
        except BaseException:
            stop_process(process)
            raise
    if process.returncode or len(response) > MAX_TRACE_BYTES:
        raise ValueError("Provider capability preflight failed")
    actual = json.loads(response)
    if actual.get("available") is not True or actual.get("provenance") != config.routes[route.value]:
        raise ValueError("Provider did not confirm exact tool provenance and action set")
    return actual


def run_adapter(config, request, directory: Path, deadline: Deadline, ledger: Ledger) -> AdapterResult:
    directory.mkdir(mode=0o700)
    home = directory / "home"
    home.mkdir(mode=0o700)
    started = time.monotonic()
    usage_start = len(ledger.usage)
    request_start = ledger.requests
    metrics_start = {**ledger.metrics, "__seen__": dict(ledger.metric_seen)}
    status = Status.INCONCLUSIVE
    provenance = {}
    answer = None
    trace = directory / "adapter-events.jsonl"
    errors = directory / "adapter-errors.log"
    with errors.open("wb") as error_stream, subprocess.Popen(
            config.command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=error_stream,
            start_new_session=True, cwd=home, env=provider_environment(config, home)) as process:
        errors.chmod(0o600)
        try:
            process.stdin.write(json.dumps(request).encode() + b"\n")
            process.stdin.flush()
            state = consume(process, request, config, trace, deadline, ledger, errors)
            status = state.status
            provenance = state.provenance
            answer = state.answer
            process.wait(timeout=deadline.require())
        except (TimeoutError, subprocess.TimeoutExpired):
            status = Status.TIMEOUT
        except (ValueError, OSError, ArithmeticError, json.JSONDecodeError):
            status = Status.INCONCLUSIVE
        finally:
            if process.poll() is None:
                stop_process(process)
    return finish_adapter_result(process.returncode, status, ledger, request, usage_start, request_start,
                                 metrics_start, provenance, started, answer)


def finish_adapter_result(exit_code, status, ledger, request, usage_start, request_start, metrics_start, provenance, started, answer):
    if exit_code != 0 and status == Status.PASSED:
        status = Status.INCONCLUSIVE
    if ledger.pending or len(ledger.usage) == usage_start:
        status = Status.INCONCLUSIVE if status != Status.TIMEOUT else status
    totals = ledger.totals(usage_start, request_start, metrics_start)
    required = ["description_bytes", "result_bytes", "tool_calls", "native_calls", "images", "image_bytes", "interventions"]
    if request["route"] == Route.WORKFLOW:
        required.append("authoring_calls")
    if status == Status.PASSED and any(totals[field] is None for field in required):
        status = Status.INCONCLUSIVE
    return AdapterResult(status, exit_code, totals, provenance, time.monotonic() - started, answer)


def consume(process, request, config, trace, deadline, ledger, errors):
    selector = selectors.DefaultSelector()
    selector.register(process.stdout, selectors.EVENT_READ)
    pending = b""
    total = 0
    provenance = {}
    status = Status.INCONCLUSIVE
    answer = None
    try:
        while True:
            deadline.require()
            if errors.stat().st_size > MAX_TRACE_BYTES:
                raise ValueError("Private trace exceeded byte cap")
            events = selector.select(min(0.2, deadline.remaining()))
            if not events:
                continue
            data = os.read(process.stdout.fileno(), 65536)
            if not data:
                if pending:
                    raise ValueError("Incomplete adapter event")
                return AdapterState(status, provenance, answer)
            total += len(data)
            if total > MAX_TRACE_BYTES:
                raise ValueError("Private trace exceeded byte cap")
            with trace.open("ab") as stream:
                stream.write(data)
            trace.chmod(0o600)
            pending += data
            while b"\n" in pending:
                line, pending = pending.split(b"\n", 1)
                event = json.loads(line)
                state = handle_event(process, event, request, config, ledger, status, provenance, answer)
                status = state.status
                provenance = state.provenance
                answer = state.answer
    finally:
        selector.close()


def handle_event(process, event, request, config, ledger, status, provenance, answer):
    kind = EventType(event.get("type"))
    if kind == EventType.PROVENANCE:
        actual = event["value"]
        if actual != config.routes[request["route"]]:
            raise ValueError("Adapter changed provider tool provenance")
        validate_provenance(Route(request["route"]), actual)
        provenance = actual
    elif kind == EventType.REQUEST:
        if not provenance or not ledger.authorize(event):
            raise ValueError("Provider request denied by private budget or unknown previous usage")
        process.stdin.write(b'{"authorized":true}\n')
        process.stdin.flush()
    elif kind in (EventType.USAGE, EventType.METRICS):
        ledger.observe(event)
    elif kind in (EventType.DESCRIPTIONS, EventType.RESULT):
        serialized = event.get("serialized")
        if not isinstance(serialized, str):
            raise ValueError("Tool metering requires the actual serialized client payload")
        payload = serialized.encode("utf-8")
        field = "description_bytes" if kind == EventType.DESCRIPTIONS else "result_bytes"
        ledger.observe({"type": "metrics", field: len(payload), "tool_calls": int(kind == EventType.RESULT)})
    elif kind == EventType.ANSWER:
        if not isinstance(event.get("value"), str):
            raise ValueError("Observation answer must be text")
        ledger.record(event)
        answer = event["value"]
    elif kind == EventType.COMPLETED:
        ledger.record(event)
        status = Status.PASSED if event.get("completed") is True else Status.FAILED
    else:
        raise ValueError("Unsupported adapter event")
    return AdapterState(status, provenance, answer)
