#!/usr/bin/env python3
"""Compile observed Tryton UI fragments into one parameterized native MCP function.

This is authoring, not routine execution. The chosen line count belongs to the
function contract; new customers, references, products and quantities are inputs.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parent
MAXIMUM_LINES = 3
MAXIMUM_STEPS = 100
FUNCTION = "tryton_create_draft_sale"
CELLS_PER_LINE = 17
PRODUCT_CELL = 2
QUANTITY_CELL = 6
SAVED_FIELDS = 17
SAVED_REFERENCE = 8
SAVED_CUSTOMER = 16


def read_fragment(name):
    return json.loads((ROOT / "fragments" / name).read_text(encoding="utf-8"))


def rename_references(value, names):
    if isinstance(value, dict):
        return {key: rename_references(child, names) for key, child in value.items()}
    if isinstance(value, list):
        return [rename_references(child, names) for child in value]
    if not isinstance(value, str):
        return value

    def replacement(match):
        name, tail = match.group(1), match.group(2)
        return "{{" + names.get(name, name) + tail + "}}"

    return re.sub(r"\{\{([a-zA-Z][a-zA-Z0-9_]*)([^{}]*)\}\}", replacement, value)


def compile_fragment(fragment, prefix, inputs):
    names = dict(inputs)
    for step in fragment["steps"]:
        if "capture" in step:
            names[step["capture"]] = prefix + step["capture"]

    steps = rename_references(fragment["steps"], names)
    for step in steps:
        if "capture" in step:
            step["capture"] = names[step["capture"]]
    conditions = rename_references(fragment["postconditions"], names)
    steps.extend({"action": "verify_element", "args": condition} for condition in conditions)

    return steps


def verified_replacements(fragment):
    fragment = copy.deepcopy(fragment)
    steps = []
    for step in fragment["steps"]:
        steps.append(step)
        if step["action"] == "type_text":
            args = step["args"]
            steps.append({"action": "verify_element", "args": {
                "id": args["id"], "expected_text": args["text"], "timeout_ms": 2000}})
    fragment["steps"] = steps

    return fragment


def final_checks(line_count):
    header = {"action": "find_elements", "args": {"pid": "{{pid}}", "role": "Edit", "visible": True,
                                                   "limit": 60}, "capture": "saved_header", "expect_count": SAVED_FIELDS}
    lines = {"action": "find_elements", "args": {"pid": "{{pid}}", "role": "table cell", "within": {"role": "table"},
                                                  "count": CELLS_PER_LINE * line_count, "limit": 100, "timeout_ms": 3000},
             "capture": "saved_lines", "expect_count": CELLS_PER_LINE * line_count}
    header_checks = [
        {"id": f"{{{{saved_header.matches.{SAVED_REFERENCE}.Id}}}}", "expected_text": "{{reference}}"},
        {"id": f"{{{{saved_header.matches.{SAVED_CUSTOMER}.Id}}}}", "expected_text": "{{customer}}"}]
    steps = [header] + [{"action": "verify_element", "args": condition} for condition in header_checks] + [lines]
    for index in range(1, line_count + 1):
        product = CELLS_PER_LINE * (index - 1) + PRODUCT_CELL
        quantity = CELLS_PER_LINE * (index - 1) + QUANTITY_CELL
        conditions = [
            {"id": f"{{{{saved_lines.matches.{product}.Id}}}}",
             "expected_text": f"{{{{line_{index}_filled.matches.8.Value}}}}"},
            {"id": f"{{{{saved_lines.matches.{quantity}.Id}}}}", "expected_text": f"{{{{quantity_{index}}}}}"}]
        steps.extend({"action": "verify_element", "args": condition} for condition in conditions)

    return steps


def compile_order(line_count):
    if type(line_count) is not int or not 1 <= line_count <= MAXIMUM_LINES:
        raise ValueError(f"Choose 1 to {MAXIMUM_LINES} lines during authoring.")

    header = verified_replacements(read_fragment("open-order.json"))
    line = verified_replacements(read_fragment("add-line.json"))
    line["steps"][-1]["args"]["name_contains"] = "[{{product}}]"
    line["steps"][-1]["expect_count"] = 1
    save = read_fragment("save-order.json")
    steps = compile_fragment(header, "header_", {})
    parameters = copy.deepcopy(header["parameters"])
    for index in range(1, line_count + 1):
        names = {"product": f"product_{index}", "quantity": f"quantity_{index}"}
        steps.extend(compile_fragment(line, f"line_{index}_", names))
        for parameter in line["parameters"]:
            if parameter["name"] in names:
                renamed = copy.deepcopy(parameter)
                renamed["name"] = names[parameter["name"]]
                parameters.append(renamed)
    steps.extend(compile_fragment(save, "save_", {}))
    steps.extend(final_checks(line_count))
    if len(steps) > MAXIMUM_STEPS:
        raise ValueError("The procedure exceeds the native macro step budget.")

    return {"version": 2, "name": FUNCTION,
            "description": f"Create and save one Tryton draft with exactly {line_count} lines. "
                           "Verify typed values, saved header, product/quantity pairs and save status. "
                           "No automatic retry; quantities are UI text. See the example's acceptance scope.",
            "parameters": parameters, "steps": steps,
            "postconditions": save["postconditions"], "sha256": ""}


def write_bundle(directory, line_count):
    recipe = compile_order(line_count)
    directory.mkdir(parents=True, exist_ok=False)
    recipe_path = directory / "create-draft-sale.json"
    data = (json.dumps(recipe, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    recipe_path.write_bytes(data)
    manifest = {
        "format": "k-lani-bot.local-macro-bundle.v1", "id": "k-lani-bot.tryton", "version": "0.3.2",
        "target": {"application": "Tryton", "process_name": "tryton", "application_version": "6.0.34-1",
                   "tested_platform": "linux-x64", "tested_os": "Ubuntu 24.04 X11/GTK3 container", "tested_locale": "en-US"},
        "entrypoints": [{"name": FUNCTION, "recipe": recipe_path.name, "topic": "Use/Sales/Order",
                         "compatibility": "ExactBuild", "validation": "unreviewed",
                         "risk": "probably_harmful", "scopes": ["observe", "ui"]}],
        "files": [{"path": recipe_path.name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}],
        "automatic_activation": False}
    manifest_path = directory / "cartridge.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    return manifest_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New isolated application-version directory; never an existing active cartridge.")
    parser.add_argument("--lines", type=int, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Use a new directory, then inspect and merge the recipe into the existing cartridge.")

    print(write_bundle(args.output, args.lines))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError) as error:
        raise SystemExit(str(error)) from None
