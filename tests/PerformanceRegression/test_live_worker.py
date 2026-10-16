"""Explicit installed-image regression; never builds an image or contacts a model."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/performance"))
from contracts import Deadline, execute
from docker_lab import copy_outputs, remove_container, start_worker
from workloads import create_workload, verify
from oracle import read_editor_text
from contracts import Suite, Status, stop_process


@unittest.skipUnless(os.environ.get("K_LANI_LAB_TEST_IMAGE"), "Explicit accepted Docker image required")
class LiveWorkerTests(unittest.TestCase):
    def test_installed_converseen_dropdown_opens_without_selecting_an_option(self):
        deadline = Deadline(60)
        with tempfile.TemporaryDirectory() as temporary:
            workload = create_workload(Suite.DESKTOP, 11, 0, Path(temporary) / "inputs-case")
            worker = start_worker({"image_digest": os.environ["K_LANI_LAB_TEST_IMAGE"]}, workload.inputs, deadline)
            try:
                probe = '''import json, subprocess, time, pyatspi
application = subprocess.Popen(["converseen"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
def nodes(element):
    yield element
    for child in element:
        yield from nodes(child)
try:
    choice = None
    for attempt in range(40):
        apps = [app for app in pyatspi.Registry.getDesktop(0) if app.name.lower() == "converseen"]
        if apps:
            choice = next((item for item in nodes(apps[0]) if item.getRoleName() == "combo box"), None)
        if choice is not None:
            break
        time.sleep(0.1)
    if choice is None:
        raise RuntimeError("Installed application dropdown is absent")
    before = choice.name
    action = choice.queryAction()
    opening = [index for index in range(action.nActions) if action.getName(index).lower() in ("press", "show menu", "open")]
    if len(opening) != 1 or not action.doAction(opening[0]):
        raise RuntimeError("No confirmed unique native dropdown opening")
    time.sleep(0.1)
    options = [item.name for item in nodes(choice) if item.getRoleName() in ("menu item", "list item")]
    if not options or choice.name != before:
        raise RuntimeError("Dropdown did not expose options or changed selection")
    print(json.dumps({"opened": True, "option_count": len(options), "selection_unchanged": True}))
finally:
    application.terminate()
    application.wait(timeout=5)
'''
                result = execute(["docker", "exec", worker.name, "/app/session-exec.sh", "python3", "-c", probe], deadline)
                self.assertEqual(result.exit_code, 0, result.output)
                actual = json.loads(result.output)
                self.assertTrue(actual["selection_unchanged"])
                self.assertGreater(actual["option_count"], 1)
            finally:
                remove_container(worker.name, deadline)

    def test_independent_oracle_reads_actual_long_mousepad_document(self):
        deadline = Deadline(60)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            workload = create_workload(Suite.RESULT, 22, 1, root / "case")
            worker = start_worker({"image_digest": os.environ["K_LANI_LAB_TEST_IMAGE"]}, workload.inputs, deadline)
            application = subprocess.Popen(["docker", "exec", worker.name, "/app/session-exec.sh"] + workload.application_command,
                                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
            try:
                actual = wait_editor(worker.name, Deadline(10))
                expected = (workload.inputs / "observation.txt").read_text()
                output = root / "outputs"
                output.mkdir()
                self.assertEqual(actual, expected)
                self.assertEqual(verify(workload, output, actual, actual).status, Status.PASSED)
                self.assertEqual(verify(workload, output, actual[:2000], actual).status, Status.FAILED)
            finally:
                stop_process(application)
                remove_container(worker.name, deadline)

    def test_fresh_private_profiles_inputs_and_actual_output_transport(self):
        deadline = Deadline(60)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            workload = create_workload(Suite.DESKTOP, 11, 0, root / "case")
            lock = {"image_digest": os.environ["K_LANI_LAB_TEST_IMAGE"]}
            workers = []
            try:
                first = start_worker(lock, workload.inputs, deadline)
                workers.append(first)
                second = start_worker(lock, workload.inputs, deadline)
                workers.append(second)
                self.assertNotEqual(first.name, second.name)
                for worker in workers:
                    identity = execute(["docker", "exec", worker.name, "id", "-u"], deadline)
                    self.assertEqual(identity.output.strip(), "10001")
                    observed = execute(["docker", "exec", worker.name, "cat", "/inputs/observation.txt"], deadline)
                    self.assertEqual(observed.output, (workload.inputs / "observation.txt").read_text())
                    inspect = execute(["docker", "inspect", worker.name], deadline)
                    metadata = json.loads(inspect.output)[0]
                    self.assertTrue(metadata["HostConfig"]["ReadonlyRootfs"])
                    self.assertFalse(metadata["HostConfig"]["Privileged"])
                    self.assertEqual(metadata["HostConfig"]["NetworkMode"], "none")
                    self.assertEqual(metadata["HostConfig"]["IpcMode"], "private")
                    binds = [mount for mount in metadata["Mounts"] if mount["Type"] == "bind"]
                    self.assertEqual(len(binds), 1)
                    self.assertEqual(binds[0]["Destination"], "/inputs")
                    self.assertFalse(binds[0]["RW"])
                execute(["docker", "exec", first.name, "touch", "/tmp/config/private-profile-marker"], deadline)
                absent = execute(["docker", "exec", second.name, "test", "!", "-e", "/tmp/config/private-profile-marker"], deadline)
                self.assertEqual(absent.exit_code, 0)
                copied = execute(["docker", "exec", first.name, "cp", "/inputs/observation.txt", "/workspace/actual.txt"], deadline)
                self.assertEqual(copied.exit_code, 0)
                copy_outputs(first, root / "outputs", deadline)
                self.assertEqual((root / "outputs/actual.txt").read_text(), observed.output)
            finally:
                for worker in workers:
                    remove_container(worker.name, deadline)


def wait_editor(worker, deadline):
    while deadline.remaining() > 0:
        try:
            return read_editor_text(worker, deadline)
        except ValueError:
            time.sleep(min(0.1, deadline.require()))
    raise TimeoutError("Live editor did not become available")


if __name__ == "__main__":
    unittest.main()
