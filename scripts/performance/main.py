"""Explicit deterministic smoke, preparation, preflight and bounded paid measurements."""
import argparse
import json
import os
import re
from pathlib import Path
import time
import uuid

from contracts import digest, LIMITS, Deadline, Comparison, Profile, Route, Status, Suite, execute, private_json
from coordinator import preflight, run_measurement
from docker_lab import prepare
from snapshot import freeze

REPOSITORY = Path(__file__).resolve().parents[2]


def arguments(argv=None):
    parser = argparse.ArgumentParser(description="Private k-lani-bot desktop lab. No model calls without an explicit paid profile.")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--prepare", action="store_true", help="Prepare image once; setup is excluded from scored latency")
    mode.add_argument("--preflight", action="store_true", help="Infrastructure, application and provider/baseline availability; no paid calls")
    mode.add_argument("--profile", type=Profile, choices=list(Profile), help="smoke: 120s/no models; quick: 600s/6 runs; matrix: 1200s/12 runs")
    parser.add_argument("--suite", type=Suite, choices=list(Suite), default=Suite.DESKTOP)
    parser.add_argument("--comparison", type=Comparison, choices=list(Comparison), default=Comparison.COMPUTER_USE)
    parser.add_argument("--route", type=Route, choices=[Route.COMPACT, Route.WORKFLOW], default=Route.COMPACT)
    parser.add_argument("--ticket", type=int)
    parser.add_argument("--baseline", help="Clean source commit; prepare that immutable baseline separately")
    parser.add_argument("--candidate", default="working-tree", help="Immutable source reference (default: frozen working-tree)")
    parser.add_argument("--model", help="Explicit supported provider model; no automatic substitution")
    parser.add_argument("--reasoning", help="Explicit provider-supported effort; no escalation")
    parser.add_argument("--seeds", default="11,22,33", help="Exactly three distinct integer seeds for one paired quick profile")
    parser.add_argument("--provider-config", type=Path, help="Private mode-600 host adapter configuration, credentials and run budget")
    parser.add_argument("--greenmail-image", help="Optional mail setup: exact GreenMail registry image@sha256 digest")
    parser.add_argument("--max-setup-seconds", type=int, default=1200, help="Setup deadline; maximum 1200 seconds")
    parser.add_argument("--output", type=Path, help="New private directory inside .artifacts; never overwritten")
    options = parser.parse_args(argv)
    if not (options.prepare or options.preflight or options.profile):
        parser.print_help()
        return None
    if not 1 <= options.max_setup_seconds <= 1200:
        parser.error("Setup deadline must be between 1 and 1200 seconds")
    try:
        options.seeds = [int(value) for value in options.seeds.split(",")]
    except ValueError:
        parser.error("Seeds must be integers")
    if len(options.seeds) != 3 or len(set(options.seeds)) != 3:
        parser.error("Exactly three distinct seeds are required")
    if options.profile in (Profile.QUICK, Profile.MATRIX):
        if options.suite == Suite.COMPATIBILITY and (options.route != Route.WORKFLOW
                                                   or options.comparison != Comparison.BEFORE_AFTER):
            parser.error("Compatibility requires learned-mcp before/after routes with the shared synthetic fixture")
        if options.profile == Profile.QUICK and (not options.model or not options.reasoning):
            parser.error("Quick requires explicit model and reasoning")
        if (options.profile == Profile.QUICK and options.comparison == "before-after"
                and (not options.baseline or not re.fullmatch(r"[a-f0-9]{40,64}", options.baseline))):
            parser.error("Before/after comparison requires the exact clean starting commit hash")
    options.limits = LIMITS.get(options.profile)
    return options


def output_directory(options):
    root = REPOSITORY / ".artifacts"
    path = options.output or root / "performance/runs" / (time.strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8])
    path = path.resolve()
    if not path.is_relative_to(root.resolve()) or path == root.resolve():
        raise ValueError("Reports and traces must stay private inside .artifacts")
    path.mkdir(parents=True, mode=0o700, exist_ok=False)
    return path


def smoke(output, deadline):
    commands = [["python3", "-m", "unittest", "discover", "-s", "tests/PerformanceRegression", "-p", "test_*.py"],
                ["dotnet", "run", "--project", "tests/PerformanceRegression/PerformanceRegression.csproj", "--verbosity", "quiet"]]
    results = []
    for index, command in enumerate(commands):
        result = execute(command, deadline, cwd=REPOSITORY, log=output / ("smoke-" + str(index) + ".log"))
        results.append({"command": command, "exit_code": result.exit_code})
    return results


def execute_selected_mode(options, output, report, deadline):
    if options.profile == Profile.SMOKE:
        report["smoke"] = smoke(output, deadline)
        report["status"] = Status.PASSED if all(item["exit_code"] == 0 for item in report["smoke"]) else Status.FAILED
    else:
        snapshot = freeze(REPOSITORY, output / "snapshot", options.candidate, deadline)
        report["source_digest"] = snapshot.digest
        if options.prepare:
            if options.suite == Suite.RUNTIME:
                from runtime_lab import prepare_runtime
                report["preparation"] = prepare_runtime(REPOSITORY, snapshot, deadline, output)
            else:
                report["preparation"] = prepare(REPOSITORY, snapshot, deadline, output, mail=options.suite == Suite.MAIL,
                                                greenmail_image=options.greenmail_image)
            report["status"] = report["preparation"]["status"]
        elif options.preflight:
            result = preflight(REPOSITORY, snapshot, options, deadline, output)
            checks = result.checks
            report["preflights"] = checks
            report["status"] = Status.PASSED if all(check.status == Status.PASSED for check in checks.values()) else Status.UNAVAILABLE
        else:
            run_measurement(REPOSITORY, snapshot, options, output, report, deadline)
        if options.candidate == "working-tree":
            current = freeze(REPOSITORY, output / "source-recheck", deadline=deadline)
            if current.digest != snapshot.digest:
                report["status"] = Status.INCONCLUSIVE
                report["performance"] = "inconclusive"
                report["source_changed"] = True
        verifier = digest((REPOSITORY / "scripts/performance/oracle.py").read_bytes())
        if verifier != report["verifier_sha256"]:
            report["status"] = Status.INCONCLUSIVE
            report["performance"] = "inconclusive"
            report["verifier_changed"] = True

def main(argv=None):
    os.umask(0o077)
    options = arguments(argv)
    if options is None:
        return 0
    started = time.monotonic()
    seconds = options.max_setup_seconds if options.prepare else options.limits.seconds if options.limits else 120
    deadline = Deadline(seconds)
    output = output_directory(options)
    report = {"schema_version": 1, "status": Status.INCONCLUSIVE, "implementation": "harness",
              "performance": "pending", "ticket": options.ticket, "suite": options.suite,
              "profile": options.profile, "limits": options.limits, "attempts": [], "usage": None,
              "savings_claim": None, "trace_location": str(output)}
    try:
        commit = execute(["git", "rev-parse", "HEAD"], deadline, cwd=REPOSITORY).output.strip()
        report["source_commit"] = commit
        report["verifier_sha256"] = digest((REPOSITORY / "scripts/performance/oracle.py").read_bytes())
        execute_selected_mode(options, output, report, deadline)
    except TimeoutError:
        report["status"] = Status.TIMEOUT
        report["performance"] = "inconclusive"
    except (ValueError, OSError, KeyError, TypeError, ArithmeticError) as exception:
        report["status"] = Status.UNAVAILABLE
        report["detail"] = str(exception)
    finally:
        report["elapsed_seconds"] = time.monotonic() - started
        private_json(output / "report.json", report)
    print(json.dumps({"status": report["status"], "report": str(output / "report.json"), "performance": report["performance"]}))
    return 0 if report["status"] == Status.PASSED else 3


if __name__ == "__main__":
    raise SystemExit(main())
