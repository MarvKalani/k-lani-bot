"""Host-only verifier: no expected answers or verifier code are mounted in model workers."""
from dataclasses import dataclass
import io
import json
from pathlib import Path
import zipfile
import xml.etree.ElementTree as ElementTree
from contracts import Status, digest, execute


@dataclass(frozen=True)
# The host verifier reports actual output correctness independently of client claims.
class Verdict:
    status: Status
    detail: str


def safe_file(path: Path, root: Path, maximum=16 * 1024 * 1024) -> bytes:
    if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()) or not path.is_file():
        raise ValueError("Output is not a regular file inside the trial")
    if path.stat().st_size > maximum:
        raise ValueError("Output exceeds verification limit")
    return path.read_bytes()


def verify_runtime_output(inputs: Path, outputs: Path, hashes: dict, expected: str) -> Verdict:
    try:
        original = safe_file(inputs / "observation.txt", inputs)
        if digest(original) != hashes["observation.txt"]:
            raise ValueError("Native runtime original input changed")
        saved = safe_file(outputs / "written.txt", outputs)
        if saved != expected.encode("utf-8"):
            raise ValueError("Actual application-saved runtime output differs from the full requested text")
        return Verdict(Status.PASSED, "Independent exact application-saved file matches the requested runtime value")
    except (ValueError, OSError, UnicodeError) as error:
        return Verdict(Status.FAILED, str(error))


def read_editor_text(worker: str, deadline) -> str:
    # The independent read-only probe contains no expected values and runs after the model has stopped.
    probe = '''import json, pyatspi
applications = [app for app in pyatspi.Registry.getDesktop(0) if app.name.lower() == "mousepad"]
if len(applications) != 1:
    raise RuntimeError("Independent editor application is absent or ambiguous")
pending = list(applications)
texts = []
visited = 0
while pending:
    element = pending.pop()
    visited += 1
    if visited > 2000:
        raise RuntimeError("Independent editor scope exceeds its bound")
    if element.getRoleName() == "text" and element.getState().contains(pyatspi.STATE_EDITABLE):
        interface = element.queryText()
        if interface.characterCount > 262144:
            raise RuntimeError("Independent editor text exceeds its bound")
        texts.append(interface.getText(0, -1))
    pending.extend(list(element))
if len(texts) != 1:
    raise RuntimeError("Independent editor field is absent or ambiguous")
print(json.dumps({"text": texts[0]}))
'''
    result = execute(["docker", "exec", worker, "/app/session-exec.sh", "python3", "-c", probe], deadline)
    if result.exit_code:
        raise ValueError("Independent editor-state probe failed")
    text = json.loads(result.output).get("text")
    if not isinstance(text, str) or len(text.encode()) > 1024 * 1024:
        raise ValueError("Independent editor-state response is invalid")
    return text


def verify_editor(inputs: Path, outputs: Path, hashes: dict, written: str | None,
                  actual: str | None, answer: str | None, read_answer: bool, refusal: Verdict | None = None) -> Verdict:
    try:
        original = safe_file(inputs / "observation.txt", inputs)
        if digest(original) != hashes["observation.txt"]:
            raise ValueError("Original document changed")
        expected = written if written is not None else original.decode("utf-8")
        if actual != expected:
            raise ValueError("Independent live editor text differs from the requested state")
        if written is not None and safe_file(outputs / "written.txt", outputs) != written.encode():
            raise ValueError("Actual saved document differs from the known written value")
        if written is None and list(outputs.iterdir()):
            raise ValueError("Read/refusal produced unexpected output files")
        if read_answer and answer != expected:
            raise ValueError("Complete unknown read was not returned")
        if written is None and not read_answer and (refusal is None or refusal.status != Status.PASSED):
            raise ValueError("Actual gateway validation-refusal evidence is absent")
        return Verdict(Status.PASSED, "Verified independent live editor state, original input and requested saved/read output")
    except (ValueError, OSError, UnicodeError) as exception:
        return Verdict(Status.FAILED, str(exception))


def verify_validation_refusal(path: Path) -> Verdict:
    try:
        events = safe_file(path, path.parent, maximum=8 * 1024 * 1024).decode().splitlines()
        for line in events:
            event = json.loads(line)
            if event.get("type") != "tool_result":
                continue
            payload = json.loads(event.get("serialized", "{}"))
            payload = payload.get("result", payload)
            if "structuredContent" in payload:
                payload = payload["structuredContent"]
            elif "content" in payload:
                text = next((part["text"] for part in payload["content"] if part.get("type") == "text"), "{}")
                payload = json.loads(text)
            if payload.get("success") is False and payload.get("error") in (
                    "Invalid result_mode.", "Unknown argument: result_mode"):
                return Verdict(Status.PASSED, "Actual serialized gateway result rejected invalid options")
        return Verdict(Status.FAILED, "No actual invalid-options tool result was recorded")
    except (ValueError, OSError, TypeError, AttributeError, KeyError) as exception:
        return Verdict(Status.FAILED, str(exception))


def compatibility_refusal_event(path: Path) -> Verdict:
    try:
        events = safe_file(path, path.parent, maximum=8 * 1024 * 1024).decode().splitlines()
        for line in events:
            event = json.loads(line)
            if event.get("type") != "tool_result":
                continue
            payload = json.loads(event.get("serialized", "{}"))
            payload = payload.get("result", payload)
            if "structuredContent" in payload:
                payload = payload["structuredContent"]
            elif "content" in payload:
                text = next((part["text"] for part in payload["content"] if part.get("type") == "text"), "{}")
                payload = json.loads(text)
            if payload.get("status") == "incompatible" and payload.get("reason") == "required_action_absent":
                return Verdict(Status.PASSED, "Actual serialized compatibility result refused the missing action")
        return Verdict(Status.FAILED, "No actual incompatible-action tool result was recorded")
    except (ValueError, OSError, TypeError, AttributeError, KeyError) as exception:
        return Verdict(Status.FAILED, str(exception))


def read_compatibility_state(worker: str, deadline) -> dict:
    probe = '''import json, pyatspi
pending = list(pyatspi.Registry.getDesktop(0))
values = {}
visited = 0
while pending:
    element = pending.pop()
    visited += 1
    if visited > 3000:
        raise RuntimeError("Independent compatibility state exceeds its bound")
    if element.name in ("Delivery context", "Saved order state"):
        if element.name in values:
            raise RuntimeError("Independent compatibility target is ambiguous")
        values[element.name] = element.queryText().getText(0, -1)
    pending.extend(list(element))
print(json.dumps(values))
'''
    result = execute(["docker", "exec", worker, "/app/session-exec.sh", "python3", "-c", probe], deadline)
    if result.exit_code:
        raise ValueError("Independent compatibility state probe failed")
    return json.loads(result.output)


def verify_compatibility_refusal(output: Path, refusal: Verdict | None, state: dict | None) -> Verdict:
    if refusal is None or refusal.status != Status.PASSED:
        return Verdict(Status.FAILED, "Actual structured compatibility refusal is required")
    if state != {"Delivery context": "Local / Collect", "Saved order state": ""}:
        return Verdict(Status.FAILED, "Actual unchanged initial application state is required")
    if (output / "result.json").exists():
        return Verdict(Status.FAILED, "Incompatible operation unexpectedly produced saved output")
    return Verdict(Status.PASSED, "Incompatible action refused with independently unchanged application state; negative case")


def read_state_discovery(worker: str, control: str, deadline) -> dict:
    # Only actual accessibility values are collected. Expectations and generated-function metadata remain on the host.
    probe = '''import json, pyatspi
pending = list(pyatspi.Registry.getDesktop(0))
values = []
visited = 0
while pending:
    element = pending.pop()
    visited += 1
    if visited > 3000:
        raise RuntimeError("Independent application-state scope exceeds its bound")
    if element.getRoleName() in ("text", "entry") and element.getState().contains(pyatspi.STATE_EDITABLE):
        interface = element.queryText()
        if interface.characterCount > 262144:
            raise RuntimeError("Independent text scope exceeds its bound")
        values.append({"name": element.name, "text": interface.getText(0, -1)})
    pending.extend(list(element))
print(json.dumps(values))
'''
    result = execute(["docker", "exec", worker, "/app/session-exec.sh", "python3", "-c", probe], deadline)
    if result.exit_code:
        raise ValueError("Independent state-discovery probe failed")
    values = json.loads(result.output)
    if control == "paged-discovery":
        fields = [item for item in values if item["name"].startswith("Field ")]
        if len(fields) != 250 or len({item["name"] for item in fields}) != 250:
            raise ValueError("Independent native field inventory is incomplete or ambiguous")
        return {item["name"]: item["text"] for item in fields}
    if len(values) != 2:
        raise ValueError("Independent editor inventory must contain two documents")
    return {"documents": [item["text"] for item in values]}


def verify_state_discovery(workload, outputs: Path, answer: str | None, state: dict | None) -> Verdict:
    try:
        for name, expected_hash in workload.hashes.items():
            if digest(safe_file(workload.inputs / name, workload.inputs)) != expected_hash:
                raise ValueError("Original discovery document changed")
        response = json.loads(answer) if isinstance(answer, str) else None
        if workload.control == "paged-discovery":
            saved = json.loads(safe_file(outputs / "result.json", outputs))
            if state != workload.expected or saved != state:
                raise ValueError("Saved and independently observed native fields differ")
            controls = response.get("controls", []) if isinstance(response, dict) else []
            functions = response.get("functions", []) if isinstance(response, dict) else []
            expected_functions = [entry["name"] for entry in workload.prepared_library["functions"]]
            if sorted(controls) != sorted(workload.expected) or sorted(functions) != sorted(expected_functions):
                raise ValueError("Full unique control/function discovery was not returned")
        else:
            if response != workload.expected or state is None or sorted(state.get("documents", [])) != sorted(workload.expected.values()):
                raise ValueError("Independent document states or complete final read differ")
            expected_files = []
            if workload.control == "state-changed":
                expected_files = ["changed-A.txt"]
                if safe_file(outputs / "changed-A.txt", outputs).decode() != workload.expected["A"]:
                    raise ValueError("Changed document was not actually saved")
            if sorted(path.name for path in outputs.iterdir()) != expected_files:
                raise ValueError("Unexpected state-discovery output files")
        return Verdict(Status.PASSED, "Verified actual application state, complete discovery/read and unchanged originals")
    except (ValueError, OSError, TypeError, AttributeError, KeyError) as exception:
        return Verdict(Status.FAILED, str(exception))


def canonical_rgba(image):
    pixels = bytearray(image.convert("RGBA").tobytes())
    for offset in range(0, len(pixels), 4):
        if pixels[offset + 3] == 0:
            pixels[offset:offset + 3] = b"\0\0\0"
    return bytes(pixels)


def verify_images(inputs: Path, outputs: Path, input_hashes: dict) -> Verdict:
    try:
        from PIL import Image
        expected_names = []
        for name, expected_hash in input_hashes.items():
            source = safe_file(inputs / name, inputs)
            if digest(source) != expected_hash:
                raise ValueError("Original or unrelated input changed")
            if not name.endswith(".png"):
                continue
            output_name = Path(name).with_suffix(".webp").name
            expected_names.append(output_name)
            converted = safe_file(outputs / output_name, outputs)
            with Image.open(io.BytesIO(source)) as original, Image.open(io.BytesIO(converted)) as actual:
                if actual.format != "WEBP" or original.size != actual.size or canonical_rgba(original) != canonical_rgba(actual):
                    raise ValueError("WebP does not match the declared lossless profile")
        archive_bytes = safe_file(outputs / "images.zip", outputs)
        with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
            if sorted(archive.namelist()) != sorted(expected_names) or archive.testzip() is not None:
                raise ValueError("ZIP entries are incorrect or corrupt")
            for name in expected_names:
                expected = safe_file(outputs / name, outputs)
                if archive.getinfo(name).file_size != len(expected) or archive.read(name) != expected:
                    raise ValueError("ZIP content differs from verified images")
        if sorted(path.name for path in outputs.iterdir()) != sorted(expected_names + ["images.zip"]):
            raise ValueError("Unexpected output files")
        return Verdict(Status.PASSED, "Verified decoded RGBA, exact ZIP entries and unchanged inputs")
    except (ValueError, OSError, zipfile.BadZipFile, ImportError) as exception:
        return Verdict(Status.FAILED, str(exception))


def verify_control(outputs: Path, expected: dict) -> Verdict:
    try:
        actual = json.loads(safe_file(outputs / "result.json", outputs))
        if actual != expected:
            raise ValueError("Persisted application state differs from requested operation")
        return Verdict(Status.PASSED, "Verified actual persisted fixture state")
    except (ValueError, OSError) as exception:
        return Verdict(Status.FAILED, str(exception))


def verify_door(outputs: Path, expected: dict) -> Verdict:
    try:
        actual = json.loads(safe_file(outputs / "result.json", outputs))
        widths = {"Basic": [900, 1000], "Wide": [1000, 1200]}
        if actual != expected or actual.get("WidthMm") not in widths.get(actual.get("Model"), []):
            raise ValueError("Persisted door differs from the requested valid configuration")
        panels = {900: ["None"], 1000: ["None", "Left", "Right"], 1200: ["None", "Left", "Right", "Both"]}
        if actual["SidePanels"] not in panels[actual["WidthMm"]]:
            raise ValueError("Persisted side panels are invalid for the actual width")
        glass = ["Clear", "Frosted"] + (["Security"] if actual["Model"] == "Wide" else [])
        required = actual["SidePanels"] in ("Left", "Both")
        if required and actual["LeftGlass"] not in glass or not required and actual["LeftGlass"] is not None:
            raise ValueError("Persisted glass does not satisfy actual visible/required rules")
        return Verdict(Status.PASSED, "Verified actual saved door dimensions, panels and required glass")
    except (ValueError, OSError, TypeError, KeyError) as exception:
        return Verdict(Status.FAILED, str(exception))


def verify_staged_delivery(outputs: Path, expected: dict) -> Verdict:
    verdict = verify_control(outputs, expected)
    if verdict.status != Status.PASSED:
        return verdict
    if expected.get("Mode") != "Delivery" or expected.get("Option") != "Express" or not expected.get("Address"):
        return Verdict(Status.FAILED, "Staged delivery requires a valid express address")
    return Verdict(Status.PASSED, "Verified actual saved express order after modal confirmation")


def verify_cell(path: Path, expected: str) -> Verdict:
    try:
        data = safe_file(path, path.parent)
        with zipfile.ZipFile(io.BytesIO(data)) as workbook:
            for name in ("xl/sharedStrings.xml", "xl/worksheets/sheet1.xml"):
                if workbook.getinfo(name).file_size > 4 * 1024 * 1024:
                    raise ValueError("Workbook verification exceeds decompression limit")
            shared = ElementTree.fromstring(workbook.read("xl/sharedStrings.xml"))
            namespace = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
            values = ["".join(item.itertext()) for item in shared.findall("s:si", namespace)]
            sheet = ElementTree.fromstring(workbook.read("xl/worksheets/sheet1.xml"))
            cell = sheet.find(".//s:c[@r='A1']", namespace)
            if cell is None or cell.get("t") != "s" or values[int(cell.find("s:v", namespace).text)] != expected:
                raise ValueError("Persisted spreadsheet cell differs")
        return Verdict(Status.PASSED, "Verified A1 in persisted XLSX")
    except (ValueError, OSError, KeyError, IndexError, AttributeError, ElementTree.ParseError, zipfile.BadZipFile) as exception:
        return Verdict(Status.FAILED, str(exception))


def verify_capabilities(directory: Path) -> Verdict:
    try:
        from PIL import Image
        source = safe_file(directory / "sample.png", directory)
        converted = safe_file(directory / "sample.webp", directory)
        with Image.open(io.BytesIO(source)) as original, Image.open(io.BytesIO(converted)) as actual:
            if actual.format != "WEBP" or actual.size != original.size:
                raise ValueError("Actual WebP output has incorrect dimensions or encoding")
            if actual.convert("RGBA").getchannel("A").tobytes() != original.convert("RGBA").getchannel("A").tobytes():
                raise ValueError("Actual WebP alpha differs")
        archive_data = safe_file(directory / "actual.zip", directory)
        with zipfile.ZipFile(io.BytesIO(archive_data)) as archive:
            source_text = safe_file(directory / "archive-input.txt", directory)
            if archive.namelist() != ["archive-input.txt"] or archive.getinfo("archive-input.txt").file_size != len(source_text):
                raise ValueError("Actual ZIP entries differ")
            if archive.read("archive-input.txt") != source_text:
                raise ValueError("Actual ZIP contents differ")
        if b"Synthetic PDF capability" not in safe_file(directory / "pdf-text.txt", directory):
            raise ValueError("Actual PDF text extraction differs")
        verdict = verify_cell(directory / "actual.xlsx", "Synthetic saved cell")
        if verdict.status != Status.PASSED:
            return verdict
        return Verdict(Status.PASSED, "Host oracle verified actual WebP, ZIP, extracted PDF text and persisted XLSX A1")
    except (ValueError, OSError, ImportError, zipfile.BadZipFile) as exception:
        return Verdict(Status.FAILED, str(exception))
