"""Deterministic regression cases; adapter fixtures never contact a provider."""
from decimal import Decimal
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/performance"))
from adapters import ProviderConfiguration, configuration, run_adapter
from contracts import Deadline, Profile, LIMITS, Route, Status, digest, execute
from coordinator import paired_cases, staged_gateway_setup, compatibility_gateway_setup, verify_trial_state
from docker_lab import Worker, accepted_image, image_lock_path, worker_command
from main import arguments, output_directory
from mail_service import service_command
from oracle import verify_control, verify_images, verify_cell, verify_validation_refusal, compatibility_refusal_event, verify_compatibility_refusal
from snapshot import Snapshot, freeze, permitted
from telemetry import Ledger, validate_provenance
from workloads import create_workload, verify
from contracts import Suite
from coordinator import run_trial

PROVENANCE = {"provider": "deterministic-test", "version": "1", "tool": "test-tool",
              "actions": ["read"], "schema_sha256": "a" * 64, "documentation": "https://example.invalid/test",
              "implementation": "local_test"}


class HarnessTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        self.ledger = Ledger(self.directory / "usage.jsonl", 100, 2, Decimal("0.10"))

    def tearDown(self):
        self.temporary.cleanup()

    def usage(self, identifier="request-1", **fields):
        event = {"type": "usage", "request_id": identifier, "input_tokens": 10, "output_tokens": 5,
                 "reference_cost": "0.01"}
        event.update(fields)
        return event

    def authorize(self):
        return self.ledger.authorize({"type": "request", "reservation": {"tokens": 30, "reference_cost": "0.02"}})

    def test_hard_caps_and_no_implicit_profile(self):
        self.assertEqual(LIMITS[Profile.QUICK].seconds, 600)
        self.assertEqual(LIMITS[Profile.QUICK].model_runs, 6)
        self.assertEqual(LIMITS[Profile.QUICK].per_run_seconds, 90)
        self.assertEqual(LIMITS[Profile.MATRIX].model_runs, 12)
        self.assertIsNone(arguments([]))
        with self.assertRaises(SystemExit):
            arguments(["--profile", "quick", "--model", "test"])
        with self.assertRaises(SystemExit):
            arguments(["--prepare", "--max-setup-seconds", "1201"])
        with self.assertRaises(SystemExit):
            arguments(["--preflight", "--seeds", "11,11,33"])

    def test_deadline_and_process_tree_timeout(self):
        now = [10.0]
        deadline = Deadline(5, lambda: now[0])
        self.assertEqual(deadline.remaining(2), 3)
        now[0] = 15
        with self.assertRaises(TimeoutError):
            deadline.require()
        with self.assertRaises(TimeoutError):
            execute([sys.executable, "-c", "import time; time.sleep(10)"], Deadline(0.05))

    def test_missing_usage_is_unknown_and_blocks_next_call(self):
        self.assertIsNone(self.ledger.totals()["input_tokens"])
        self.assertTrue(self.authorize())
        self.assertFalse(self.authorize())
        self.assertIsNone(self.ledger.totals()["output_tokens"])
        self.assertIsNone(self.ledger.totals()["model_calls"])
        self.assertEqual(self.ledger.totals()["authorized_provider_requests"], 1)

    def test_reasoning_is_not_added_to_output_and_images_not_inferred(self):
        self.authorize()
        self.ledger.observe(self.usage(reasoning_tokens=3, cached_input_tokens=7))
        self.ledger.observe({"type": "metrics", "image_bytes": 1000000})
        totals = self.ledger.totals()
        self.assertEqual(totals["output_tokens"], 5)
        self.assertEqual(totals["reasoning_tokens"], 3)
        self.assertIsNone(totals["image_tokens"])
        self.assertEqual(totals["image_bytes"], 1000000)

    def test_budget_is_not_expanded_and_duplicate_usage_is_rejected(self):
        self.authorize()
        self.ledger.observe(self.usage())
        with self.assertRaises(ValueError):
            self.ledger.observe(self.usage())
        self.assertTrue(self.authorize())
        self.ledger.observe(self.usage("request-2"))
        self.assertFalse(self.authorize())
        self.assertEqual(json.loads(self.ledger.path.read_text().splitlines()[-1])["type"], "request")
        self.assertFalse(self.ledger.authorize({"type": "request", "reservation": {"tokens": 1000, "reference_cost": "1000"}}))

    def test_missing_or_invalid_usage_never_becomes_zero(self):
        self.authorize()
        with self.assertRaises(ValueError):
            self.ledger.observe(self.usage(input_tokens=None))
        self.assertIsNone(self.ledger.totals()["input_tokens"])
        with self.assertRaises(ValueError):
            self.ledger.observe(self.usage(image_tokens=-1))

    def test_fake_computer_use_and_missing_capabilities_are_rejected(self):
        with self.assertRaises(ValueError):
            validate_provenance(Route.COMPUTER, PROVENANCE)
        with self.assertRaises(ValueError):
            validate_provenance(Route.COMPACT, {})
        actual = {**PROVENANCE, "implementation": "authentic_provider"}
        validate_provenance(Route.COMPUTER, actual)
        self.assertEqual(actual["actions"], ["read"])

    def test_paired_order_and_phase_count(self):
        plan = paired_cases([11, 22, 33], "computer-use", Route.COMPACT)
        self.assertEqual(len(plan), 6)
        self.assertEqual([item["arm"] for item in plan], ["baseline", "candidate", "candidate", "baseline", "baseline", "candidate"])
        self.assertEqual([item["case"] for item in plan if item["arm"] == "candidate"], [0, 1, 2])

    def test_worker_has_no_host_display_credentials_or_oracle(self):
        first = worker_command("sha256:test", "trial-1", self.directory)
        second = worker_command("sha256:test", "trial-2", self.directory)
        self.assertNotEqual(first, second)
        self.assertIn("--read-only", first)
        self.assertIn("10001:10001", first)
        self.assertIn("none", first)
        self.assertIn("--pull=never", first)
        self.assertIn("--ipc=private", first)
        self.assertNotIn("--env", first)
        self.assertNotIn("--volume", first)
        self.assertEqual(sum("type=bind" in part for part in first), 1)
        self.assertTrue(any("dst=/inputs,readonly" in part for part in first))
        self.assertFalse(any("docker.sock" in part or "X11-unix" in part or "oracle" in part for part in first))

    def test_mail_service_cannot_publish_ports_or_reach_external_servers(self):
        command = service_command("registry@sha256:" + "a" * 64, "mail-trial", "private-network")
        self.assertIn("private-network", command)
        self.assertNotIn("--publish", command)
        self.assertNotIn("-p", command)
        self.assertNotIn("--volume", command)
        self.assertIn("--pull=never", command)
        self.assertTrue(any("lab.test" in part for part in command))

    def test_snapshot_excludes_private_runtime_and_golden_data(self):
        for name in [".internal/answer.json", ".artifacts/trace.json", "src/node/obj/state.json", "tools/golden/answer.json",
                     "scripts/performance/oracle.py", "modules/foo/credentials.json", "src/../.env"]:
            self.assertFalse(permitted(name), name)
        self.assertTrue(permitted("docker/performance/Dockerfile"))
        self.assertTrue(permitted("tools/k-lani-bot-cartridge-lab/DynamicFormModel.cs"))
        repository = self.directory / "repo"
        repository.mkdir()
        subprocess.run(["git", "init", "-q", str(repository)], check=True)
        source = repository / "src/test.cs"
        source.parent.mkdir()
        source.write_text("public class Synthetic {}")
        snapshot = freeze(repository, self.directory / "frozen")
        source.write_text("changed")
        changed = freeze(repository, self.directory / "changed")
        self.assertNotEqual(snapshot.digest, changed.digest)
        self.assertEqual(snapshot.files[0]["sha256"], digest(b"public class Synthetic {}"))
        source.unlink()
        source.symlink_to("/etc/passwd")
        with self.assertRaises(ValueError):
            freeze(repository, self.directory / "linked")

    def test_wrong_fixture_output_fails_even_after_reported_success(self):
        expected = {"Mode": "Delivery", "Option": "Express", "Address": "Example Road", "Revision": 1}
        (self.directory / "result.json").write_text(json.dumps({**expected, "Option": "Courier"}))
        self.assertEqual(verify_control(self.directory, expected).status, Status.FAILED)
        (self.directory / "result.json").write_text(json.dumps(expected))
        self.assertEqual(verify_control(self.directory, expected).status, Status.PASSED)
        (self.directory / "result.json").unlink()
        (self.directory / "result.json").symlink_to("/etc/passwd")
        self.assertEqual(verify_control(self.directory, expected).status, Status.FAILED)

    def test_wrong_cell_fails_independent_saved_workbook_oracle(self):
        output = self.directory / "actual.xlsx"
        with zipfile.ZipFile(output, "w") as workbook:
            workbook.writestr("xl/sharedStrings.xml", '<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>wrong</t></si></sst>')
            workbook.writestr("xl/worksheets/sheet1.xml", '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row><c r="A1" t="s"><v>0</v></c></row></sheetData></worksheet>')
        self.assertEqual(verify_cell(output, "correct").status, Status.FAILED)
        self.assertEqual(verify_cell(output, "wrong").status, Status.PASSED)

    def test_adapter_crash_retains_usage_and_failed_attempt(self):
        script = self.directory / "adapter.py"
        script.write_text('''import sys, json
request = json.loads(sys.stdin.readline())
print(json.dumps({"type":"provenance", "value": request["provenance"]}), flush=True)
print(json.dumps({"type":"request", "reservation":{"tokens":30,"reference_cost":"0.02"}}), flush=True)
sys.stdin.readline()
print(json.dumps({"type":"usage","request_id":"crash","input_tokens":10,"output_tokens":5,"reference_cost":"0.01"}), flush=True)
raise SystemExit(9)
''')
        config = ProviderConfiguration([sys.executable, str(script)], "test-client", [], [], {Route.COMPACT: PROVENANCE},
                                       100, 2, Decimal("0.10"), "USD", [])
        request = {"route": Route.COMPACT, "provenance": PROVENANCE}
        result = run_adapter(config, request, self.directory / "adapter-run", Deadline(2), self.ledger)
        self.assertEqual(result.status, Status.INCONCLUSIVE)
        self.assertEqual(result.exit_code, 9)
        self.assertEqual(result.totals["input_tokens"], 10)
        self.assertTrue((self.directory / "adapter-run/adapter-events.jsonl").exists())

    def completed_adapter(self, include_metrics):
        script = self.directory / "complete.py"
        script.write_text('''import sys, json
request = json.loads(sys.stdin.readline())
print(json.dumps({"type":"provenance","value":request["provenance"]}), flush=True)
print(json.dumps({"type":"request","reservation":{"tokens":30,"reference_cost":"0.02"}}), flush=True)
sys.stdin.readline()
print(json.dumps({"type":"usage","request_id":"complete","input_tokens":10,"output_tokens":5,"reference_cost":"0.01"}), flush=True)
if request["include_metrics"]:
    print(json.dumps({"type":"tool_descriptions","serialized":"native read: café"}), flush=True)
    print(json.dumps({"type":"tool_result","serialized":"observed text"}), flush=True)
    print(json.dumps({"type":"metrics","native_calls":1,"images":0,"image_bytes":0,"interventions":0}), flush=True)
print(json.dumps({"type":"answer","value":"observed text"}), flush=True)
print(json.dumps({"type":"completed","completed":True}), flush=True)
''')
        config = ProviderConfiguration([sys.executable, str(script)], "test-client", [], [], {Route.COMPACT: PROVENANCE},
                                       100, 2, Decimal("0.10"), "USD", [])
        request = {"route": Route.COMPACT, "provenance": PROVENANCE, "include_metrics": include_metrics}
        return run_adapter(config, request, self.directory / "completed-run", Deadline(2), self.ledger)

    def test_complete_adapter_records_exact_utf8_payload_and_observation(self):
        result = self.completed_adapter(True)
        self.assertEqual(result.status, Status.PASSED)
        self.assertEqual(result.totals["description_bytes"], len("native read: café".encode("utf-8")))
        self.assertEqual(result.totals["result_bytes"], len("observed text".encode("utf-8")))
        self.assertEqual(result.totals["images"], 0)
        self.assertIsNone(result.totals["image_tokens"])
        self.assertEqual(result.answer, "observed text")

    def test_missing_tool_telemetry_is_inconclusive_instead_of_zero(self):
        result = self.completed_adapter(False)
        self.assertEqual(result.status, Status.INCONCLUSIVE)
        self.assertIsNone(result.totals["description_bytes"])
        self.assertIsNone(result.totals["native_calls"])
        self.assertEqual(result.totals["input_tokens"], 10)

    def test_adapter_timeout_retains_partial_record_and_unknown_usage(self):
        script = self.directory / "timeout.py"
        script.write_text('''import sys,json,time
request = json.loads(sys.stdin.readline())
print(json.dumps({"type":"provenance", "value":request["provenance"]}), flush=True)
print(json.dumps({"type":"request", "reservation":{"tokens":30,"reference_cost":"0.02"}}), flush=True)
sys.stdin.readline()
time.sleep(10)
''')
        config = ProviderConfiguration([sys.executable, str(script)], "test-client", [], [], {Route.COMPACT: PROVENANCE},
                                       100, 2, Decimal("0.10"), "USD", [])
        result = run_adapter(config, {"route": Route.COMPACT, "provenance": PROVENANCE}, self.directory / "timeout-run",
                             Deadline(0.1), self.ledger)
        self.assertEqual(result.status, Status.TIMEOUT)
        self.assertIsNone(result.totals["input_tokens"])
        self.assertIsNone(result.totals["model_calls"])
        self.assertEqual(result.totals["authorized_provider_requests"], 1)

    def test_application_preflight_rejects_missing_accessibility_codec_or_archive(self):
        snapshot = Snapshot("source", self.directory / "source.tar", [], "worker")
        path = image_lock_path(self.directory, snapshot)
        path.parent.mkdir(parents=True)
        checks = {name: {"status": "passed"} for name in ["webp", "zip", "text_pdf", "spreadsheet"]}
        lock = {"source_digest": "source", "worker_source_digest": "worker", "status": "passed",
                "image_digest": "sha256:test", "capabilities": {"checks": checks,
                "independent_verification": {"status": "passed"}}}
        for missing in ["webp", "zip", "text_pdf"]:
            checks[missing]["status"] = "unavailable"
            path.write_text(json.dumps(lock))
            with self.assertRaises(ValueError):
                accepted_image(self.directory, snapshot, Deadline(1))
            checks[missing]["status"] = "passed"
        lock["capabilities"]["independent_verification"]["status"] = "failed"
        path.write_text(json.dumps(lock))
        with self.assertRaises(ValueError):
            accepted_image(self.directory, snapshot, Deadline(1))

    def test_shared_prompt_and_synthetic_input_permissions(self):
        previous = os.umask(0o077)
        try:
            baseline = create_workload(Suite.DESKTOP, 11, 0, self.directory / "baseline")
            candidate = create_workload(Suite.DESKTOP, 11, 0, self.directory / "candidate")
        finally:
            os.umask(previous)
        self.assertEqual(baseline.prompt, candidate.prompt)
        self.assertEqual((baseline.inputs / "observation.txt").stat().st_mode & 0o777, 0o644)
        self.assertEqual(baseline.inputs.stat().st_mode & 0o777, 0o755)
        answer = (baseline.inputs / "observation.txt").read_text()
        self.assertEqual(verify(baseline, self.directory, answer).status, Status.PASSED)
        self.assertEqual(verify(baseline, self.directory, answer[:-2]).status, Status.FAILED)

    def test_result_contract_is_separate_and_verifies_full_saved_and_live_values(self):
        workload = create_workload(Suite.RESULT, 11, 0, self.directory / "write-case")
        output = self.directory / "written-output"
        output.mkdir()
        text = workload.expected["text"]
        (output / "written.txt").write_text(text)
        self.assertEqual(verify(workload, output, editor_text=text).status, Status.PASSED)
        self.assertEqual(verify(workload, output, editor_text=text[:-1]).status, Status.FAILED)
        self.assertEqual(verify(workload, output).status, Status.FAILED)
        (output / "written.txt").write_text(text[:-1])
        self.assertEqual(verify(workload, output, editor_text=text).status, Status.FAILED)
        legacy = create_workload(Suite.DESKTOP, 11, 0, self.directory / "desktop-case")
        self.assertNotEqual(legacy.control, workload.control)
        self.assertNotEqual(legacy.prompt, workload.prompt)

    def test_result_contract_unknown_read_and_refusal_require_unchanged_actual_content(self):
        output = self.directory / "read-output"
        output.mkdir()
        workload = create_workload(Suite.RESULT, 22, 1, self.directory / "read-case")
        text = (workload.inputs / "observation.txt").read_text()
        self.assertGreater(len(text), 2000)
        self.assertIn("😀", text)
        self.assertEqual(verify(workload, output, text, text).status, Status.PASSED)
        self.assertEqual(verify(workload, output, text[:2000], text).status, Status.FAILED)
        self.assertEqual(verify(workload, output, text, "changed").status, Status.FAILED)
        refusal = create_workload(Suite.RESULT, 33, 2, self.directory / "refusal-case")
        original = (refusal.inputs / "observation.txt").read_text()
        trace = self.directory / "adapter-events.jsonl"
        trace.write_text(json.dumps({"type": "answer", "value": "Invalid result_mode."}) + "\n")
        self.assertEqual(verify_validation_refusal(trace).status, Status.FAILED)
        self.assertEqual(verify(refusal, output, editor_text=original).status, Status.FAILED)
        trace.write_text(json.dumps({"type": "tool_result", "serialized": json.dumps({
            "content": [{"type": "text", "text": json.dumps({"success": False, "error": "Invalid result_mode."})}]})}) + "\n")
        evidence = verify_validation_refusal(trace)
        self.assertEqual(verify(refusal, output, editor_text=original, refusal=evidence).status, Status.PASSED)
        self.assertEqual(verify(refusal, output, editor_text="changed").status, Status.FAILED)
        (refusal.inputs / "observation.txt").write_text("changed")
        self.assertEqual(verify(refusal, output, editor_text=original).status, Status.FAILED)
        with self.assertRaises(ValueError):
            create_workload(Suite.RESULT, 33, 3, self.directory / "bad-case")

    def test_state_discovery_dispatch_and_independent_documents_reject_wrong_claims(self):
        output = self.directory / "state-output"
        output.mkdir()
        for case in (0, 1):
            workload = create_workload(Suite.STATE, 11, case, self.directory / ("state-" + str(case)))
            self.assertEqual(len(workload.additional_application_commands), 1)
            if case == 1:
                (output / "changed-A.txt").write_text(workload.expected["A"])
            state = {"documents": list(workload.expected.values())}
            answer = json.dumps(workload.expected)
            self.assertEqual(verify(workload, output, answer, state=state).status, Status.PASSED)
            self.assertEqual(verify(workload, output, answer, state={"documents": ["wrong", "claim"]}).status, Status.FAILED)
            self.assertEqual(verify(workload, output, answer).status, Status.FAILED)
        parsed = arguments(["--preflight", "--suite", "state-discovery"])
        self.assertEqual(parsed.suite, Suite.STATE)

    def test_discovery_catalog_has_120_parameterized_functions_and_complete_actual_inventory_is_required(self):
        workload = create_workload(Suite.STATE, 22, 2, self.directory / "discovery")
        self.assertEqual(len(workload.expected), 250)
        functions = workload.prepared_library["functions"]
        self.assertEqual(len(functions), 120)
        self.assertNotIn(workload.expected["Field 119"], json.dumps(workload.prepared_library))
        root = self.directory / "catalog"
        file = root / "programs/discovery-fixture/1.0/generated-ui.json"
        file.parent.mkdir(parents=True)
        file.write_text(json.dumps(workload.prepared_library))
        result = subprocess.run(["node", str(ROOT / "scripts/performance/validate-library.mjs"), str(root), "[]"], capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        output = self.directory / "discovery-output"
        output.mkdir()
        (output / "result.json").write_text(json.dumps(workload.expected))
        answer = {"controls": list(workload.expected), "functions": [entry["name"] for entry in functions]}
        self.assertEqual(verify(workload, output, json.dumps(answer), state=workload.expected).status, Status.PASSED)
        answer["functions"] = answer["functions"][:50]
        self.assertEqual(verify(workload, output, json.dumps(answer), state=workload.expected).status, Status.FAILED)
        (output / "result.json").write_text(json.dumps({**workload.expected, "Field 001": "wrong"}))
        self.assertEqual(verify(workload, output, json.dumps(answer), state=workload.expected).status, Status.FAILED)

    def test_door_form_suite_and_saved_output_validate_real_rules(self):
        self.assertEqual(arguments(["--preflight", "--suite", "dynamic-forms"]).suite, Suite.FORMS)
        for case in range(3):
            workload = create_workload(Suite.FORMS, 11, case, self.directory / ("door-case-" + str(case)))
            output = self.directory / ("door-output-" + str(case))
            output.mkdir()
            (output / "result.json").write_text(json.dumps(workload.expected))
            self.assertEqual(verify(workload, output).status, Status.PASSED)
            wrong = {**workload.expected, "LeftGlass": "Security" if case != 1 else None}
            (output / "result.json").write_text(json.dumps(wrong))
            self.assertEqual(verify(workload, output).status, Status.FAILED)
        with self.assertRaises(ValueError):
            create_workload(Suite.FORMS, 11, 3, self.directory / "invalid-door-case")

    def test_staged_workflow_phases_have_new_inputs_and_independent_saved_state_verification(self):
        self.assertEqual(arguments(["--preflight", "--suite", "staged-workflows"]).suite, Suite.WORKFLOWS)
        addresses = []
        for case, seed in enumerate([11, 22, 33]):
            workload = create_workload(Suite.WORKFLOWS, seed, case, self.directory / ("staged-case-" + str(case)))
            output = self.directory / ("staged-output-" + str(case))
            output.mkdir()
            addresses.append(workload.expected["Address"])
            (output / "result.json").write_text(json.dumps(workload.expected))
            self.assertEqual(verify(workload, output).status, Status.PASSED)
            (output / "result.json").write_text(json.dumps({**workload.expected, "Option": "Courier"}))
            self.assertEqual(verify(workload, output).status, Status.FAILED)
        self.assertEqual(len(set(addresses)), 3)

    def test_untrusted_success_does_not_override_actual_wrong_output(self):
        from adapters import AdapterResult
        options = arguments(["--profile", "quick", "--model", "test", "--reasoning", "medium"])
        trial = {"case": 1, "seed": 22, "arm": "candidate", "route": Route.COMPACT,
                 "model": "test", "reasoning": "medium"}
        lock = {"image_digest": "sha256:test", "source_digest": "source", "capabilities": {"fingerprints": {}}}
        result = AdapterResult(Status.PASSED, 0, {}, PROVENANCE, 0.1, None)
        attempt = {}
        def copy_wrong_output(worker, output, deadline):
            output.mkdir()
            (output / "result.json").write_text(json.dumps({"Mode": "Delivery", "Option": "Courier", "Address": "wrong", "Revision": 1}))
        with patch("coordinator.start_worker", return_value=Worker("unit-only-worker", "sha256:test")), \
                patch("coordinator.subprocess.Popen") as application, \
                patch("coordinator.run_adapter", return_value=result), \
                patch("coordinator.copy_outputs", side_effect=copy_wrong_output), \
                patch("coordinator.remove_container"):
            application.return_value.poll.return_value = 0
            run_trial(lock, type("TestClient", (), {"client_version": "unit-only"})(), self.ledger, options, trial, attempt, self.directory, Deadline(20))
        self.assertEqual(attempt["verification"].status, Status.FAILED)
        self.assertNotEqual(attempt["status"], Status.PASSED)

    def test_incremental_usage_totals_do_not_duplicate_earlier_trials(self):
        self.authorize()
        self.ledger.observe(self.usage())
        self.authorize()
        self.ledger.observe(self.usage("request-2", input_tokens=20))
        self.assertEqual(self.ledger.totals()["input_tokens"], 30)
        self.assertEqual(self.ledger.totals(since=1, request_start=1)["input_tokens"], 20)
        self.assertEqual(self.ledger.totals(since=1, request_start=1)["model_calls"], 1)

    def test_reusable_library_rejects_native_ids_and_revision_directories(self):
        library = self.directory / "library"
        file = library / "programs/synthetic/1.0/generated-ui.json"
        file.parent.mkdir(parents=True)
        document = {"format": "k-lani-bot-generated-ui-functions", "version": 1, "revision": 1,
                    "application": {"key": "synthetic", "version": "1.0", "identity_source": "controlled-test-fixture"},
                    "functions": [], "candidates": []}
        file.write_text(json.dumps(document))
        command = ["node", str(ROOT / "scripts/performance/validate-library.mjs"), str(library), "[]"]
        self.assertEqual(execute(command, Deadline(2)).exit_code, 0)
        document["native_id"] = 33
        file.write_text(json.dumps(document))
        self.assertNotEqual(execute(command, Deadline(2)).exit_code, 0)
        del document["native_id"]
        file.unlink()
        revision_file = file.parent / "revision-2/generated-ui.json"
        revision_file.parent.mkdir()
        revision_file.write_text(json.dumps(document))
        self.assertNotEqual(execute(command, Deadline(2)).exit_code, 0)

    def test_staged_gateway_setup_requires_observed_authority_and_separate_private_jobs(self):
        setup = staged_gateway_setup()
        workflow = setup["workflow_setup"]
        self.assertNotEqual(workflow["stateRoot"], workflow["cartridgeRoot"])
        self.assertEqual(workflow["authority_source"], "authorized_node_principal_connection")
        self.assertNotIn("authority", workflow)
        self.assertEqual(setup["learning"]["application"], workflow["applications"][0]["application"])
        self.assertFalse(setup["learning"]["auto_capture"])

    def test_compatibility_suite_has_declared_patch_metadata_and_a_separate_negative_oracle(self):
        self.assertEqual(arguments(["--preflight", "--suite", "compatibility"]).suite, Suite.COMPATIBILITY)
        with self.assertRaises(SystemExit):
            arguments(["--profile", "quick", "--suite", "compatibility", "--model", "model", "--reasoning", "low"])
        for case in (0, 1):
            workload = create_workload(Suite.COMPATIBILITY, 11 + case, case, self.directory / ("compat-" + str(case)))
            output = self.directory / ("compat-output-" + str(case))
            output.mkdir()
            (output / "result.json").write_text(json.dumps(workload.expected))
            self.assertEqual(verify(workload, output).status, Status.PASSED)
            (output / "result.json").write_text('{}')
            self.assertEqual(verify(workload, output).status, Status.FAILED)
            setup = compatibility_gateway_setup(case)
            self.assertIn("synthetic metadata", setup["fixture_provenance"])
            self.assertEqual(setup["shared_fixture_proxy"], "modules/desktop-ui-mcp/compatibility-core.mjs")
        workload = create_workload(Suite.COMPATIBILITY, 33, 2, self.directory / "compat-negative")
        output = self.directory / "compat-negative-output"
        output.mkdir()
        events = self.directory / "compat-events.jsonl"
        events.write_text(json.dumps({"type": "tool_result", "serialized": json.dumps({
            "status": "incompatible", "reason": "required_action_absent"})}) + '\n')
        refusal = compatibility_refusal_event(events)
        state = {"Delivery context": "Local / Collect", "Saved order state": ""}
        self.assertEqual(verify(workload, output, refusal=refusal, state=state).status, Status.PASSED)
        self.assertEqual(verify(workload, output, answer='incompatible', state=state).status, Status.FAILED)
        self.assertEqual(verify(workload, output, refusal=refusal).status, Status.FAILED)
        state["Saved order state"] = "changed"
        self.assertEqual(verify_compatibility_refusal(output, refusal, state).status, Status.FAILED)

    def test_compatibility_coordinator_retains_independent_negative_state(self):
        from types import SimpleNamespace
        workload = create_workload(Suite.COMPATIBILITY, 33, 2, self.directory / "coordinator-negative")
        directory = self.directory / "coordinator-result"
        (directory / "outputs").mkdir(parents=True)
        state = {"Delivery context": "Local / Collect", "Saved order state": ""}
        from oracle import Verdict
        with patch('oracle.compatibility_refusal_event', return_value=Verdict(Status.PASSED, "actual event")):
            with patch('oracle.read_compatibility_state', return_value=state):
                result = verify_trial_state(SimpleNamespace(suite=Suite.COMPATIBILITY), workload, Worker('private-worker', 'image'),
                                            directory, SimpleNamespace(answer=None), Deadline(20))
        self.assertEqual(result.status, Status.PASSED)

    def test_workflow_library_accepts_verified_bindings_and_rejects_runtime_state_and_training_values(self):
        workload = create_workload(Suite.STATE, 22, 2, self.directory / "workflow-catalog")
        workload.prepared_library["functions"][0]["validation"] = "demonstrated_once; every execution verifies outputs"
        root = self.directory / "workflow-library"
        directory = root / "programs/discovery-fixture/1.0"
        directory.mkdir(parents=True)
        (directory / "generated-ui.json").write_text(json.dumps(workload.prepared_library))
        entry = workload.prepared_library["functions"][0]
        parameter = entry["inputSchema"]["properties"]["parameters"]["required"][0]
        stage = {"id": "write", "function_ref": {"application": "fixture", "name": entry["name"]},
                 "bindings": {parameter: {"input": "text"}}, "preconditions": "fresh_unique_enabled_targets",
                 "postconditions": "recorded_function_outputs"}
        definition = {"format": "k-lani-bot-workflow/1", "key": "write-flow", "revision": 1,
                      "inputs": {"text": {"type": "string"}}, "applications": {"fixture": {
                          "key": "discovery-fixture", "version": "1.0", "window": "main"}},
                      "stages": [stage], "outputs": {"result": {"stage": "write", "output": entry["outputs"][0]["name"]}}}
        document = {"format": "k-lani-bot-workflows/1", "application": {"key": "discovery-fixture", "version": "1.0"},
                    "workflows": [definition]}
        file = directory / "workflows.json"
        command = ["node", str(ROOT / "scripts/performance/validate-library.mjs"), str(root), '["PRIVATE_TRAINING"]']
        file.write_text(json.dumps(document))
        result = execute(command, Deadline(2))
        self.assertEqual(result.exit_code, 0, result.output)
        for extra in [{"pid": 123}, {"expression": "PRIVATE_TRAINING"}]:
            stage.update(extra)
            file.write_text(json.dumps(document))
            self.assertNotEqual(execute(command, Deadline(2)).exit_code, 0)
            for key in extra:
                del stage[key]
        stage["decision"] = {"input": "text", "prompt": "PRIVATE_TRAINING"}
        file.write_text(json.dumps(document))
        self.assertNotEqual(execute(command, Deadline(2)).exit_code, 0)

    def test_private_output_and_provider_configuration(self):
        with self.assertRaises(ValueError):
            output_directory(arguments(["--preflight", "--output", str(self.directory / "public")]))
        config = self.directory / "provider.json"
        config.write_text("{}")
        config.chmod(0o644)
        with self.assertRaises(ValueError):
            configuration(config)

    def test_image_oracle_detects_wrong_archive_and_preserves_alpha(self):
        from PIL import Image
        inputs = self.directory / "inputs"
        outputs = self.directory / "outputs"
        inputs.mkdir()
        outputs.mkdir()
        image = Image.new("RGBA", (8, 9), (25, 20, 17, 0))
        image.save(inputs / "Größe image.png")
        image.save(outputs / "Größe image.webp", lossless=True)
        hashes = {"Größe image.png": digest((inputs / "Größe image.png").read_bytes())}
        with zipfile.ZipFile(outputs / "images.zip", "w") as archive:
            archive.write(outputs / "Größe image.webp", "Größe image.webp")
        self.assertEqual(verify_images(inputs, outputs, hashes).status, Status.PASSED)
        with zipfile.ZipFile(outputs / "images.zip", "w") as archive:
            archive.writestr("wrong.webp", b"fabricated")
        self.assertEqual(verify_images(inputs, outputs, hashes).status, Status.FAILED)
        (outputs / "Größe image.webp").write_bytes(b"corrupt")
        self.assertEqual(verify_images(inputs, outputs, hashes).status, Status.FAILED)


if __name__ == "__main__":
    unittest.main()
