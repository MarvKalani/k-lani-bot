"""Checks the executable example's parameterization, bounds and native bundle integrity."""
import hashlib
import json
from pathlib import Path
import re
import tempfile
import unittest

from build import compile_order, write_bundle


class AuthoringExampleTests(unittest.TestCase):
    def test_one_complete_operation_contains_no_training_data(self):
        recipe = compile_order(2)
        names = [parameter["name"] for parameter in recipe["parameters"]]
        self.assertEqual(names, ["pid", "customer", "reference", "product_1", "quantity_1", "product_2", "quantity_2"])
        encoded = json.dumps(recipe)
        for private_value in ["Baeckerei Sonne", "Cafe Mond", "AUTHOR-44", "Administrator [EUR]"]:
            self.assertNotIn(private_value, encoded)
        self.assertEqual(recipe["name"], "tryton_create_draft_sale")
        self.assertLessEqual(len(recipe["steps"]), 100)

    def test_every_capture_and_placeholder_is_bound_in_execution_order(self):
        for count in [1, 2, 3]:
            recipe = compile_order(count)
            known = {parameter["name"] for parameter in recipe["parameters"]}
            for step in recipe["steps"]:
                used = re.findall(r"\{\{([a-zA-Z][a-zA-Z0-9_]*)", json.dumps(step["args"]))
                self.assertTrue(set(used) <= known, (count, step))
                if "capture" in step:
                    self.assertNotIn(step["capture"], known)
                    known.add(step["capture"])

    def test_all_literal_writes_are_verified_before_another_input(self):
        steps = compile_order(2)["steps"]
        for index, step in enumerate(steps):
            if step["action"] != "type_text":
                continue
            after = steps[index + 1]
            self.assertEqual(after["action"], "verify_element")
            self.assertEqual(after["args"]["id"], step["args"]["id"])
            self.assertEqual(after["args"]["expected_text"], step["args"]["text"])

    def test_invalid_cardinality_is_refused(self):
        for count in [0, 4, -1, True, "2"]:
            with self.assertRaises(ValueError):
                compile_order(count)

    def test_id_checks_use_the_most_recent_observation(self):
        observed = None
        for step in compile_order(2)["steps"]:
            if step["action"] == "find_elements":
                observed = step.get("capture")
            if step["action"] == "verify_element" and "id" in step["args"]:
                reference = re.match(r"\{\{([a-zA-Z0-9_]+)\.", step["args"]["id"])
                self.assertIsNotNone(reference)
                self.assertEqual(reference.group(1), observed)

    def test_existing_output_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            with self.assertRaises(FileExistsError):
                write_bundle(directory, 2)

    def test_bundle_pins_actual_recipe_and_observed_build(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "programs" / "tryton" / "6.0.34-1"
            manifest = json.loads(write_bundle(directory, 2).read_text())
            self.assertEqual(manifest["target"]["application_version"], "6.0.34-1")
            entry = manifest["entrypoints"][0]
            data = (directory / entry["recipe"]).read_bytes()
            pin = manifest["files"][0]
            self.assertEqual(pin["sha256"], hashlib.sha256(data).hexdigest())
            self.assertEqual(pin["bytes"], len(data))
            self.assertEqual(entry["name"], json.loads(data)["name"])
            self.assertEqual(entry["compatibility"], "ExactBuild")
            self.assertFalse(manifest["automatic_activation"])


if __name__ == "__main__":
    unittest.main()
