"""Exercise run attribution and private lifecycle guards without starting a model."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/performance"))
from chat_usage import TOKEN_FIELDS, capture, difference, reported_usage
from remote_lab import owns_supervisor, private_directory, new_operation_id, exchange, configure_gateway
from contracts import Deadline
from unittest.mock import patch
import subprocess


# The host runner checks telemetry provenance independently of application contents.
class RemoteControlTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.thread = str(uuid.uuid4())
        self.file = self.root / ("rollout-2026-10-08-" + self.thread + ".jsonl")

    def tearDown(self):
        self.temporary.cleanup()

    def write_usage(self, usage):
        event = {"type": "event_msg", "payload": {"type": "token_count", "info": {"total_token_usage": usage}}}
        self.file.write_text(json.dumps(event) + "\n")

    def test_reported_counters_are_not_estimated_from_unrelated_text(self):
        self.write_usage({"input_tokens": 100, "cached_input_tokens": 80, "output_tokens": 20})
        with self.file.open("a") as stream:
            stream.write(json.dumps({"type": "message", "payload": {"text": "unrelated" * 1000}}) + "\n")
        usage = reported_usage(self.root, self.thread)
        self.assertEqual(usage["input_tokens"], 100)
        self.assertIsNone(usage["reasoning_output_tokens"])
        with self.assertRaises(ValueError):
            reported_usage(self.root, str(uuid.uuid4()))

    def test_missing_reset_and_invalid_usage_stay_unknown(self):
        values = difference({"input_tokens": 50, "output_tokens": 10}, {"input_tokens": 45, "output_tokens": 25})
        self.assertIsNone(values["input_tokens"])
        self.assertIsNone(values["cached_input_tokens"])
        self.assertEqual(values["output_tokens"], 15)
        self.write_usage({"input_tokens": -1})
        with self.assertRaises(ValueError):
            reported_usage(self.root, self.thread)

    def test_missing_and_ambiguous_telemetry_are_refused(self):
        self.file.write_text('{"type":"message","payload":{"text":"not usage"}}\n')
        with self.assertRaises(ValueError):
            reported_usage(self.root, self.thread)
        self.write_usage({"input_tokens": 100})
        duplicate = self.root / ("rollout-duplicate-" + self.thread + ".jsonl")
        duplicate.write_text(self.file.read_text())
        with self.assertRaises(ValueError):
            reported_usage(self.root, self.thread)

    def test_run_cannot_finish_with_another_threads_usage(self):
        self.write_usage({"input_tokens": 100})
        capture(self.root, "begin", "one-write", self.thread, self.root)
        other_thread = str(uuid.uuid4())
        other_file = self.root / ("rollout-other-" + other_thread + ".jsonl")
        other_file.write_text(self.file.read_text())
        with self.assertRaises(ValueError):
            capture(self.root, "finish", "one-write", other_thread, self.root)
        self.assertFalse((self.root / "one-write-usage.json").exists())

    def test_run_boundaries_preserve_identity_and_partial_scope(self):
        self.write_usage({"input_tokens": 100, "output_tokens": 20})
        capture(self.root, "begin", "one-write", self.thread, self.root)
        with self.assertRaises(ValueError):
            capture(self.root, "begin", "one-write", self.thread, self.root)
        self.write_usage({"input_tokens": 150, "output_tokens": 30})
        result = capture(self.root, "finish", "one-write", self.thread, self.root)
        self.assertEqual(result["usage"]["input_tokens"], 50)
        self.assertEqual(result["usage"]["output_tokens"], 10)
        self.assertFalse(result["usageComplete"])
        self.assertIsNone(result["apiCharge"])
        self.assertEqual(result["performance"], "inconclusive")
        with self.assertRaises(ValueError):
            capture(self.root, "finish", "../outside", self.thread, self.root)

    def test_cleanup_has_valid_fresh_operation_ids_and_keeps_stderr_separate(self):
        first = uuid.UUID(new_operation_id())
        second = uuid.UUID(new_operation_id())
        self.assertEqual(first.version, 7)
        self.assertNotEqual(first, second)
        with patch("remote_lab.subprocess.Popen") as factory:
            process = factory.return_value.__enter__.return_value
            process.communicate.return_value = (b'{"success":true}\n{"success":true}\n', None)
            process.returncode = 0
            result = exchange(["unused"], {"action": "list_windows", "args": {}}, Deadline(1), self.root)
            sent = process.communicate.call_args.args[0].decode().splitlines()
            cleanup = json.loads(sent[1])
            self.assertEqual(cleanup["action"], "end_session")
            self.assertEqual(uuid.UUID(cleanup["args"]["request_id"]).version, 7)
            self.assertEqual(len(result.output.splitlines()), 2)
            self.assertNotEqual(factory.call_args.kwargs["stderr"], subprocess.STDOUT)

    def test_meter_uses_only_selected_ui_tools_and_private_metrics(self):
        with patch("remote_lab.shutil.which", return_value="/usr/bin/node"):
            configure_gateway(self.root, Path("/private/bot-node"), {"registry": "/private/node-state/nodes.json"})
        meter = json.loads((self.root / "compact-meter.json").read_text())
        self.assertEqual(meter["tools"], ["ui_info", "ui_observe", "ui_act"])
        self.assertEqual(meter["metricsFile"], str(self.root / "mcp-metrics.json"))
        self.assertNotIn("credential_environment", meter)
        self.assertEqual((self.root / "compact-meter.json").stat().st_mode & 0o077, 0)

    def test_unrelated_supervisor_and_public_state_are_refused(self):
        record = {"pid": 0, "binary": "/unknown-node"}
        self.assertFalse(owns_supervisor(record, self.root))
        self.assertFalse(owns_supervisor({}, self.root))
        self.assertFalse(owns_supervisor({"pid": "../other", "binary": "/unknown"}, self.root))
        with self.assertRaises(ValueError):
            private_directory(self.root)


if __name__ == "__main__":
    unittest.main()
