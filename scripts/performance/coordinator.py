"""Bounded paired trials retain every attempt, including failed startup and interrupted usage."""
from dataclasses import dataclass
import json
from pathlib import Path
import io
import tarfile
import shutil
import subprocess
import time

from adapters import ProviderConfiguration, availability, check_combination, configuration, run_adapter
from contracts import CLEANUP_SECONDS, Check, Deadline, Profile, Route, Status, Suite, execute, private_json
from docker_lab import export_directory, accepted_image, copy_outputs, infrastructure, remove_container, start_worker
from snapshot import freeze, materialize_tool_source
from telemetry import Ledger, prompt_hash
from workloads import create_workload, verify


@dataclass(frozen=True)
# The coordinator separates infrastructure, application and provider availability.
class PreflightResult:
    checks: dict[str, Check]
    image: dict | None
    provider: ProviderConfiguration | None


def preflight(repository, snapshot, options, deadline, output):
    checks = {"infrastructure": infrastructure(deadline)}
    lock = None
    provider = None
    if checks["infrastructure"].status == Status.PASSED:
        try:
            if options.suite == Suite.RUNTIME:
                from runtime_lab import accepted_runtime
                lock = accepted_runtime(repository, snapshot, deadline)
            else:
                lock = accepted_image(repository, snapshot, deadline, options.suite == Suite.MAIL)
            checks["applications"] = Check(Status.PASSED, "Accepted image with actual application round trips")
        except (ValueError, OSError, TimeoutError) as exception:
            checks["applications"] = Check(Status.UNAVAILABLE, str(exception))
    else:
        checks["applications"] = Check(Status.UNAVAILABLE, "Requires reachable Docker daemon and prepared image")
    try:
        if options.provider_config is None:
            raise ValueError("Private provider configuration and explicit budget are required")
        provider = configuration(options.provider_config)
        combinations = provider.matrix if options.profile == Profile.MATRIX else [{"model": options.model, "reasoning": options.reasoning}]
        if options.profile == Profile.MATRIX and len(combinations) != 6:
            raise ValueError("Matrix requires six explicit model/effort configurations")
        for combination in combinations:
            if not combination["model"] or not combination["reasoning"]:
                raise ValueError("Explicit model and reasoning are required")
            for route in selected_routes(options):
                availability(provider, combination["model"], combination["reasoning"], route, deadline, output)
        checks["model_baseline"] = Check(Status.PASSED, "Provider confirmed selected combinations and exact route provenance with one shared model client")
    except (ValueError, OSError, KeyError, TimeoutError, TypeError, ArithmeticError):
        checks["model_baseline"] = Check(Status.UNAVAILABLE,
            "Authorized private configuration/budget or supported provider/baseline provenance is unavailable")
    if options.suite == Suite.MAIL:
        accepted = lock and lock.get("mail_service", {}).get("status") == "passed"
        checks["mail_service"] = Check(Status.PASSED if accepted else Status.UNAVAILABLE,
            "Accepted isolated SMTP/IMAP round trip" if accepted else "Requires prepared GreenMail digest and accepted isolated IMAP round trip")
        checks["mail_workload"] = Check(Status.UNAVAILABLE, "End-to-end Thunderbird receipt workload is provided separately")
    return PreflightResult(checks, lock, provider)


def selected_routes(options):
    if options.profile == Profile.MATRIX:
        return [options.route]
    if options.comparison == "computer-use":
        return [Route.COMPUTER, options.route]
    return [options.route]


def paired_cases(seeds, comparison, route):
    routes = [Route.COMPUTER, route] if comparison == "computer-use" else [route, route]
    cases = []
    for case, seed in enumerate(seeds):
        order = [0, 1] if case % 2 == 0 else [1, 0]
        for arm in order:
            cases.append({"case": case, "seed": seed, "arm": "baseline" if arm == 0 else "candidate",
                          "route": routes[arm]})
    return cases


def run_measurement(repository, snapshot, options, output, report, deadline):
    result = preflight(repository, snapshot, options, deadline, output)
    checks = result.checks
    lock = result.image
    provider = result.provider
    report["preflights"] = checks
    if any(check.status != Status.PASSED for check in checks.values()):
        report["status"] = Status.UNAVAILABLE
        return
    candidate_tools = materialize_tool_source(snapshot, output / "candidate-tools")
    baseline_tools = candidate_tools
    baseline_lock = lock
    if options.profile == Profile.QUICK and options.comparison == "before-after":
        baseline = freeze(repository, output / "baseline-snapshot", options.baseline, deadline)
        if options.suite == Suite.RUNTIME:
            from runtime_lab import accepted_runtime
            baseline_lock = accepted_runtime(repository, baseline, deadline)
        else:
            baseline_lock = accepted_image(repository, baseline, deadline)
        if lock["capabilities"]["fingerprints"]["packages_sha256"] != baseline_lock["capabilities"]["fingerprints"]["packages_sha256"]:
            raise ValueError("Matched routes require identical application package builds")
        report["baseline_source_digest"] = baseline.digest
        baseline_tools = materialize_tool_source(baseline, output / "baseline-tools")
    report["image"] = lock
    report["model_client"] = provider.client_version
    report["reference_currency"] = provider.currency
    ledger = Ledger(output / "usage.jsonl", provider.max_tokens, provider.max_calls, provider.max_cost)
    plan = paired_cases(options.seeds, options.comparison, options.route)
    if options.profile == Profile.MATRIX:
        if len(provider.matrix) != 6:
            raise ValueError("Matrix requires exactly six explicitly configured model/effort configurations")
        plan = [{"case": case, "seed": options.seeds[case], "arm": "candidate", "route": options.route,
                 "model": value["model"], "reasoning": value["reasoning"]}
                for value in provider.matrix for case in range(2)]
    run_planned_trials(plan, baseline_lock, lock, baseline_tools, candidate_tools, provider, ledger,
                       options, output, report, deadline)


def run_planned_trials(plan, baseline_lock, lock, baseline_tools, candidate_tools, provider, ledger, options, output, report, deadline):
    for trial in plan:
        deadline.require(CLEANUP_SECONDS + 5)
        if len(report["attempts"]) >= options.limits.model_runs:
            break
        trial.setdefault("model", options.model)
        trial.setdefault("reasoning", options.reasoning)
        check_combination(provider, trial["model"], trial["reasoning"], trial["route"])
        attempt = {**trial, "status": Status.INCONCLUSIVE, "usage": None}
        report["attempts"].append(attempt)
        private_json(output / "report.json", report)
        trial_lock = baseline_lock if trial["arm"] == "baseline" else lock
        trial["tool_source"] = str(baseline_tools if trial["arm"] == "baseline" else candidate_tools)
        run_trial(trial_lock, provider, ledger, options, trial, attempt, output, deadline)
        report["usage"] = ledger.totals()
        private_json(output / "report.json", report)
        if ledger.pending or ledger.stopped or ledger.requests >= ledger.max_calls or attempt["status"] == Status.TIMEOUT:
            break
    report["usage"] = ledger.totals()
    complete = len(report["attempts"]) == len(plan) and all(attempt["status"] == Status.PASSED for attempt in report["attempts"])
    report["status"] = Status.PASSED if complete else Status.INCONCLUSIVE
    report["performance"] = "measured" if complete else "inconclusive"
    report["savings_claim"] = None


def run_trial(lock, provider, ledger, options, trial, attempt, output, deadline):
    directory = output / ("trial-" + str(len(list(output.glob("trial-*"))) + 1))
    directory.mkdir(mode=0o700)
    workload = create_workload(options.suite, trial["seed"], trial["case"], directory)
    worker = None
    applications = []
    start = time.monotonic()
    try:
        worker = start_worker(lock, workload.inputs, Deadline(deadline.require(CLEANUP_SECONDS)))
        library = output / ("saved-definitions-" + trial["arm"])
        if trial["route"] == Route.WORKFLOW or options.suite == Suite.RUNTIME:
            library.mkdir(exist_ok=True, mode=0o700)
            restore_definitions(worker, library, deadline)
        if workload.prepared_library is not None:
            seed_discovery_library(workload, worker, directory, deadline)
        attempt["worker"] = worker.name
        attempt["image_digest"] = lock["image_digest"]
        # Worker entrypoint owns the bus; reuse it for the application's accessibility session.
        for arguments in [workload.application_command, *workload.additional_application_commands]:
            command = ["docker", "exec", worker.name, "/app/session-exec.sh"] + arguments
            applications.append(subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True))
        execute_trial(lock, provider, ledger, options, trial, attempt, worker, workload, library, directory, deadline)
    except (ValueError, OSError, TimeoutError):
        attempt["status"] = Status.INCONCLUSIVE
    finally:
        for application in applications:
            from contracts import stop_process
            if application.poll() is None:
                stop_process(application)
        if worker:
            remove_container(worker.name, deadline)
        attempt["elapsed_seconds"] = time.monotonic() - start


def execute_trial(lock, provider, ledger, options, trial, attempt, worker, workload, library, directory, deadline):
    request = {"mode": "run", "worker": worker.name, "route": trial["route"], "model": trial["model"],
               "reasoning": trial["reasoning"], "prompt": workload.prompt, "client_version": provider.client_version,
               "retry_count": 0,
               "tool_policy": "Only worker application tools; no host shell, oracle, provider secrets or synthetic output helpers",
               "budgets": {"seconds": options.limits.per_run_seconds, "tokens": ledger.max_tokens,
                           "calls": ledger.max_calls, "reference_cost": str(ledger.max_cost)}}
    request["application_info"] = {"platform": "linux-x11-atspi", "builds": lock["capabilities"]["fingerprints"]}
    attempt["prompt_sha256"] = prompt_hash(workload.prompt)
    attempt["control"] = workload.control
    request["source_digest"] = lock["source_digest"]
    request["tool_source"] = trial.get("tool_source")
    request["cartridge_root"] = "/tmp/cartridges" if trial["route"] == Route.WORKFLOW else None
    if workload.prepared_library is not None:
        request["cartridge_root"] = "/tmp/cartridges"
        request["learning"] = {"application": workload.prepared_library["application"], "auto_capture": False}
    if options.suite == Suite.WORKFLOWS and trial["route"] == Route.WORKFLOW:
        request.update(staged_gateway_setup())
    if options.suite == Suite.COMPATIBILITY:
        request.update(compatibility_gateway_setup(trial["case"]))
    if options.suite == Suite.RUNTIME:
        request["native_runtime"] = {"profile": "compact" if trial["arm"] == "candidate" else "javascript-reference",
                                     "controller_binary": "/app/k-lani-bot-node", "cartridge_root": "/tmp/cartridges",
                                     "learning_root": "/tmp/learning", "model_client_location": "authorized-host"}
    request["phase"] = "L" + str(trial["case"] + 1)
    result = run_adapter(provider, request, directory / "adapter", Deadline(min(
        options.limits.per_run_seconds, deadline.require(CLEANUP_SECONDS + 3))), ledger)
    attempt["adapter"] = result
    attempt["usage"] = result.totals
    copy_outputs(worker, directory / "outputs", Deadline(deadline.require(CLEANUP_SECONDS)))
    if trial["route"] == Route.WORKFLOW or options.suite == Suite.RUNTIME:
        save_definitions(repository=Path(__file__).resolve().parents[2], worker=worker,
                         library=library, workload=workload, directory=directory, deadline=deadline)
    verdict = verify_trial_state(options, workload, worker, directory, result, deadline)
    attempt["verification"] = verdict
    attempt["business_task"] = workload.control not in ("invalid-result-options", "compatibility-incompatible")
    attempt["status"] = Status.PASSED if result.status == Status.PASSED and verdict.status == Status.PASSED else Status.INCONCLUSIVE
    if result.status == Status.TIMEOUT:
        attempt["status"] = Status.TIMEOUT


def verify_trial_state(options, workload, worker, directory, result, deadline):
    editor_text = None
    if options.suite == Suite.RESULT:
        from oracle import read_editor_text
        editor_text = read_editor_text(worker.name, Deadline(deadline.require(CLEANUP_SECONDS)))
    refusal = None
    state = None
    if workload.control == "invalid-result-options":
        from oracle import verify_validation_refusal
        refusal = verify_validation_refusal(directory / "adapter/adapter-events.jsonl")
    if workload.control == "compatibility-incompatible":
        from oracle import compatibility_refusal_event, read_compatibility_state
        refusal = compatibility_refusal_event(directory / "adapter/adapter-events.jsonl")
        state = read_compatibility_state(worker.name, Deadline(deadline.require(CLEANUP_SECONDS)))
    if options.suite == Suite.STATE:
        from oracle import read_state_discovery
        state = read_state_discovery(worker.name, workload.control, Deadline(deadline.require(CLEANUP_SECONDS)))
    return verify(workload, directory / "outputs", result.answer, editor_text, refusal, state)


def compatibility_gateway_setup(case):
    if case not in (0, 1, 2):
        raise ValueError("Invalid compatibility phase")
    return {"learning": {"application": {"key": "delivery-fixture", "version": "1.1",
                                           "identity_source": "controlled-test-fixture"}, "auto_capture": False},
            "cartridge_root": "/tmp/cartridges",
            "compatibilityFixture": ["unchanged", "compatible-patch", "incompatible-action"][case],
            "shared_fixture_proxy": "modules/desktop-ui-mcp/compatibility-core.mjs",
            "fixture_provenance": "synthetic metadata builds; real native application outputs forwarded unchanged"}


def staged_gateway_setup():
    application = {"key": "delivery-fixture", "version": "1.1", "identity_source": "controlled-test-fixture"}
    return {"learning": {"application": application, "auto_capture": False},
            "workflow_setup": {"stateRoot": "/tmp/workflow-jobs", "cartridgeRoot": "/tmp/cartridges",
                               "applications": [{"application": application, "cartridgeRoot": "/tmp/cartridges"}],
                               "authority_source": "authorized_node_principal_connection"}}


def seed_discovery_library(workload, worker, directory, deadline):
    root = directory / "prepared-catalog"
    application = workload.prepared_library["application"]
    file = root / "programs" / application["key"] / application["version"] / "generated-ui.json"
    private_json(file, workload.prepared_library)
    result = execute(["node", str(Path(__file__).parent / "validate-library.mjs"), str(root), "[]"], deadline)
    if result.exit_code:
        raise ValueError("Prepared discovery catalog failed the cartridge contract")
    restore_definitions(worker, root, deadline)

def save_definitions(repository, worker, library, workload, directory, deadline):
    exported = directory / "exported-definitions"
    export_directory(worker.name, "/tmp/cartridges", exported, deadline)
    literals = [value for value in workload.expected.values() if isinstance(value, str) and value.startswith("Example Road")]
    if workload.control == "one-read":
        literals.append((workload.inputs / "observation.txt").read_text().strip())
    if workload.control in ("known-large-write", "unknown-long-read", "invalid-result-options", "native-runtime-lifecycle"):
        literals.extend(value for value in workload.expected.values() if isinstance(value, str) and value)
        observed = (workload.inputs / "observation.txt").read_text().strip()
        if observed:
            literals.append(observed)
    checked = execute(["node", str(repository / "scripts/performance/validate-library.mjs"), str(exported), json.dumps(literals)], deadline)
    if checked.exit_code:
        raise ValueError("Exported library contains unsupported or literal runtime state")
    shutil.rmtree(library)
    shutil.copytree(exported, library)


def restore_definitions(worker, library: Path, deadline: Deadline):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w") as archive:
        for path in sorted(library.rglob("*")):
            if path.is_symlink():
                raise ValueError("Reusable definitions reject symbolic links")
            if path.is_file():
                content = path.read_bytes()
                item = tarfile.TarInfo(path.relative_to(library).as_posix())
                item.size = len(content)
                item.mode = 0o600
                archive.addfile(item, io.BytesIO(content))
                if buffer.tell() > 4 * 1024 * 1024:
                    raise ValueError("Reusable definitions exceed the byte cap")
    command = ["docker", "exec", "--interactive", worker.name, "tar", "--no-same-owner", "-xf", "-", "-C", "/tmp/cartridges"]
    result = execute(command, deadline, input_data=buffer.getvalue())
    if result.exit_code:
        raise ValueError("Reusable definitions could not be restored into the fresh private worker")
