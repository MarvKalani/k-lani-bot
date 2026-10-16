"""Deterministic runtime contracts; no Docker build or model is triggered by this suite."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/performance"))
from contracts import Deadline, Status, Suite, digest
from main import arguments
from oracle import verify_runtime_output
from runtime_lab import RUNTIME_NAMES, accepted_runtime, lock_path
from runtime_mcp import MAXIMUM_OBSERVED_PAGES, declared_text_parameter, observed_elements, select_editor
from snapshot import Snapshot, worker_source
from workloads import Control, create_workload


class ScriptedClient:
    """Answers each MCP call with the next prepared reply and records what was asked."""

    def __init__(self, replies):
        self.replies = list(replies)
        self.calls = []

    def call(self, name, arguments):
        self.calls.append((name, arguments))
        return self.replies.pop(0)


def page(elements, cursor=None):
    return {"elements": elements, "truncated": cursor is not None, "next_cursor": cursor}


MOUSEPAD = {"pid": 7, "process_name": "mousepad", "version": "0.6.1-1build2", "title": "document.txt - Mousepad"}
EDITOR_VIEW = [{"role": "Window", "name": "document.txt - Mousepad"}, {"role": "Edit", "name": ""}]


class ObservedEditorTests(unittest.TestCase):
    def test_visible_view_is_requested_and_follows_the_cursor_to_the_end(self):
        client = ScriptedClient([page([{"role": "Window"}], "next-page"), page([{"role": "Edit"}])])
        elements = observed_elements(client, 7)
        self.assertEqual([element["role"] for element in elements], ["Window", "Edit"])
        first, second = client.calls
        self.assertEqual(first[1]["offscreen"], False)
        self.assertEqual(first[1]["limit"], 200)
        self.assertEqual(second[1]["cursor"], "next-page")

    def test_view_cut_off_by_the_page_budget_is_never_evaluated(self):
        client = ScriptedClient([page([{"role": "MenuItem"}], "more")] * (MAXIMUM_OBSERVED_PAGES + 1))
        with self.assertRaises(ValueError):
            observed_elements(client, 7)
        self.assertEqual(len(client.calls), MAXIMUM_OBSERVED_PAGES)

    def test_editor_needs_one_known_version_one_field_and_one_owning_scope(self):
        selected = select_editor([MOUSEPAD], EDITOR_VIEW)
        self.assertEqual((selected[0]["pid"], selected[1]["role"], selected[2]["role"]), (7, "Edit", "Window"))
        self.assertIsNone(select_editor([{**MOUSEPAD, "version": "unknown"}], EDITOR_VIEW))
        self.assertIsNone(select_editor([MOUSEPAD, MOUSEPAD], EDITOR_VIEW))
        self.assertIsNone(select_editor([MOUSEPAD], EDITOR_VIEW + [{"role": "Edit"}]))
        self.assertIsNone(select_editor([MOUSEPAD], [{"role": "Edit"}]))
        self.assertIsNone(select_editor([], EDITOR_VIEW))

    def test_reuse_parameter_comes_from_the_declared_schema(self):
        def browsed(properties):
            return {"function": {"inputSchema": {"properties": {"parameters": {"properties": properties}}}}}

        self.assertEqual(declared_text_parameter(browsed({"text": {"type": "string"}})), "text")
        self.assertEqual(declared_text_parameter(browsed({"body": {"type": "string"}})), "body")
        for refused in ({}, {"a": {"type": "string"}, "b": {"type": "string"}}, {"count": {"type": "integer"}}):
            with self.assertRaises(ValueError):
                declared_text_parameter(browsed(refused))


class NativeRuntimeTests(unittest.TestCase):
    def test_suite_cli_and_independent_target(self):
        options = arguments(["--preflight", "--suite", "native-runtime"])
        self.assertEqual(options.suite, Suite.RUNTIME)
        self.assertTrue(worker_source("docker/runtime/Dockerfile"))
        self.assertTrue(worker_source("docker/runtime/dependencies.lock.json"))
        self.assertFalse(worker_source("scripts/performance/oracle.py"))
        recipe = (ROOT / "docker/runtime/Dockerfile").read_text()
        final = recipe.split(" AS runtime", 1)[1]
        self.assertNotIn("python3-pyatspi", final)
        self.assertNotIn("tools/k-lani-bot-cartridge-lab", final)
        self.assertIn("/node/k-lani-bot-node", final)
        self.assertIn("! command -v python3", final)

    def test_saved_file_oracle_verifies_full_value_and_preserves_input(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            workload = create_workload(Suite.RUNTIME, 22, 0, directory / "case")
            self.assertEqual(workload.control, Control.RUNTIME)
            output = directory / "outputs"
            output.mkdir()
            expected = workload.expected["text"]
            file = output / "written.txt"
            for wrong in ("", "old" + expected, expected[:-1], expected + "extra"):
                file.write_text(wrong)
                self.assertEqual(verify_runtime_output(workload.inputs, output, workload.hashes, expected).status, Status.FAILED)
            file.write_text(expected)
            self.assertEqual(verify_runtime_output(workload.inputs, output, workload.hashes, expected).status, Status.PASSED)
            (workload.inputs / "observation.txt").write_text("changed")
            self.assertEqual(verify_runtime_output(workload.inputs, output, workload.hashes, expected).status, Status.FAILED)

    def test_runtime_lock_is_separate_and_requires_all_acceptance_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            repository = Path(temporary)
            snapshot = Snapshot("source", repository / "snapshot.tar", [], "worker")
            path = lock_path(repository, snapshot)
            self.assertEqual(path.name, "native-runtime.json")
            path.parent.mkdir(parents=True)
            lock = {"target": "native-runtime", "worker_source_digest": "worker", "source_digest": "source",
                    "image_digest": "sha256:image", "status": "passed", "qualification": {
                        "status": "passed", "absent_runtimes": list(RUNTIME_NAMES), "independent_saved_file": True,
                        "controller_restart": True, "form_scan": True, "verified_first_use": True, "verified_reuse": True}}
            with patch("runtime_lab.inspect_image", return_value={"Id": "sha256:image"}):
                path.write_text(json.dumps(lock))
                self.assertEqual(accepted_runtime(repository, snapshot, Deadline(1))["image_digest"], "sha256:image")
                for field, value in (("absent_runtimes", ["python3"]), ("independent_saved_file", False), ("status", "unavailable")):
                    changed = json.loads(json.dumps(lock))
                    changed["qualification"][field] = value
                    path.write_text(json.dumps(changed))
                    with self.assertRaises(ValueError):
                        accepted_runtime(repository, snapshot, Deadline(1))
                path.write_text(json.dumps({**lock, "worker_source_digest": "different"}))
                with self.assertRaises(ValueError):
                    accepted_runtime(repository, snapshot, Deadline(1))

    def test_three_declared_phases_are_same_app_scope_and_no_worker_golden_answers(self):
        with tempfile.TemporaryDirectory() as temporary:
            workloads = [create_workload(Suite.RUNTIME, 11, case, Path(temporary) / str(case)) for case in range(3)]
            self.assertTrue(all(workload.application_command[0] == "mousepad" for workload in workloads))
            self.assertIn("capture", workloads[0].prompt)
            self.assertTrue(all("reuse" in workload.prompt for workload in workloads[1:]))
            self.assertTrue(all(workload.expected["text"] not in (workload.inputs / "observation.txt").read_text()
                                for workload in workloads))
