"""Non-model setup probes operate real applications through semantic accessibility."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import subprocess
import time
import zipfile

import pyatspi
from PIL import Image
from reportlab.pdfgen import canvas

ROOT = Path("/tmp/capabilities")
ROOT.mkdir(exist_ok=True)
PROCESSES = []


def launch(command):
    process = subprocess.Popen(command, start_new_session=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    PROCESSES.append(process)
    return process


def nodes(root):
    yield root
    for index in range(root.childCount):
        child = root.getChildAtIndex(index)
        if child:
            yield from nodes(child)


def find(process, predicate, seconds=8):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        for application in pyatspi.Registry.getDesktop(0):
            if application.get_process_id() == process.pid:
                for element in nodes(application):
                    if predicate(element):
                        return element
        time.sleep(0.1)
    raise RuntimeError("Required semantic application control is unavailable")


def action(element):
    actions = element.queryAction()
    preferred = ("click", "press", "activate", "toggle", "showmenu")
    for index in range(actions.nActions):
        if actions.getName(index).lower() in preferred:
            if actions.doAction(index):
                return
    raise RuntimeError("Semantic action is unavailable")


def named(process, name):
    return find(process, lambda element: element.name == name)


def wait_file(path):
    end = time.monotonic() + 10
    while time.monotonic() < end:
        if path.exists() and path.stat().st_size:
            return
        time.sleep(0.1)
    raise RuntimeError("Application did not persist its output")


def require_keyboard_target(process):
    active_pid = subprocess.check_output(["xdotool", "getactivewindow", "getwindowpid"], timeout=5).decode().strip()
    if active_pid != str(process.pid):
        raise RuntimeError("Fresh keyboard target is not Converseen")


def focus_format(process):
    for attempt in range(20):
        element = find(process, lambda item: item.getRoleName() == "combo box")
        if element.getState().contains(pyatspi.STATE_FOCUSED):
            return
        require_keyboard_target(process)
        subprocess.run(["xdotool", "key", "--clearmodifiers", "Tab"], check=True, timeout=5)
    raise RuntimeError("Documented Tab navigation did not reach the format field")


def convert_with_keyboard(process):
    control = find(process, lambda element: element.getRoleName() == "menu item" and element.name == "Convert")
    actions = control.queryAction()
    bindings = [actions.getKeyBinding(index) for index in range(actions.nActions)]
    if not any("F5" in binding for binding in bindings):
        raise RuntimeError("Installed application does not expose the documented Convert shortcut")
    require_keyboard_target(process)
    subprocess.run(["xdotool", "key", "--clearmodifiers", "F5"], check=True, timeout=5)


def open_image(process, source):
    require_keyboard_target(process)
    subprocess.run(["xdotool", "key", "--clearmodifiers", "ctrl+o"], check=True, timeout=5)
    entry = find(process, lambda element: element.getRoleName() == "text" and element.name == "File name:")
    if not entry.queryEditableText().setTextContents(str(source)):
        raise RuntimeError("Native file chooser refused the synthetic input path")
    action(named(process, "Open"))
    find(process, lambda element: source.name in element.name)


def webp():
    source = ROOT / "sample.png"
    Image.new("RGBA", (16, 16), (255, 0, 0, 128)).save(source)
    process = launch(["converseen"])
    find(process, lambda element: element.getRoleName() == "combo box")
    open_image(process, source)
    same_directory = named(process, "Image's directory")
    if not same_directory.getState().contains(pyatspi.STATE_CHECKED):
        action(same_directory)
    if not named(process, "Image's directory").getState().contains(pyatspi.STATE_CHECKED):
        raise RuntimeError("Requested output directory was not selected")
    formats = find(process, lambda element: element.getRoleName() == "combo box")
    option = next(element for element in nodes(formats) if element.name.startswith("WEBP"))
    print("Converseen stage: format focus", file=sys.stderr, flush=True)
    formats.queryComponent().grabFocus()
    print("Converseen stage: keyboard navigation", file=sys.stderr, flush=True)
    focus_format(process)
    require_keyboard_target(process)
    subprocess.run(["xdotool", "type", "--clearmodifiers", "--delay", "30", "WEBP"], check=True, timeout=5)
    selected = find(process, lambda element: element.getRoleName() == "combo box" and element.name == option.name)
    if selected.name != option.name:
        raise RuntimeError("Observed format selection differs")
    print("Converseen stage: check all", file=sys.stderr, flush=True)
    action(named(process, "Check all"))
    print("Converseen stage: convert", file=sys.stderr, flush=True)
    convert_with_keyboard(process)
    output = ROOT / "sample.webp"
    wait_file(output)
    with Image.open(output) as image:
        if image.size != (16, 16) or image.convert("RGBA").getchannel("A").getextrema() != (128, 128):
            raise RuntimeError("WebP dimensions or alpha were not preserved")
    return "Converseen persisted decodable WebP with original dimensions and alpha; no lossless claim"


def archive():
    source = ROOT / "archive-input.txt"
    source.write_text("synthetic archive probe\n")
    process = launch(["xarchiver", "--compress", str(source)])
    entry = find(process, lambda element: element.getRoleName() == "text" and element.getState().contains(pyatspi.STATE_EDITABLE))
    entry.queryEditableText().setTextContents(str(ROOT / "actual.zip"))
    action(named(process, "Create"))
    output = ROOT / "actual.zip"
    wait_file(output)
    with zipfile.ZipFile(output) as stream:
        if stream.namelist() != [source.name] or stream.read(source.name) != source.read_bytes():
            raise RuntimeError("Xarchiver persisted incorrect archive contents")
    return "Xarchiver created ZIP; independently read exact entry and content"


def text_pdf():
    source = ROOT / "text.txt"
    source.write_text("synthetic text capability\n")
    process = launch(["mousepad", str(source)])
    find(process, lambda element: element.getRoleName() == "text"
         and "synthetic text capability" in element.queryText().getText(0, -1))
    document = ROOT / "sample.pdf"
    writer = canvas.Canvas(str(document))
    writer.drawString(20, 100, "Synthetic PDF capability")
    writer.save()
    text = subprocess.check_output(["pdftotext", str(document), "-"], timeout=10).decode()
    (ROOT / "pdf-text.txt").write_text(text)
    if "Synthetic PDF capability" not in text:
        raise RuntimeError("PDF extraction did not preserve text")
    return "Mousepad exposed actual text through AT-SPI; Poppler read generated PDF"


def spreadsheet():
    import uno
    from com.sun.star.connection import NoConnectException
    launch(["libreoffice", "-env:UserInstallation=file:///tmp/calc-profile", "--headless", "--norestore",
            "--accept=socket,host=127.0.0.1,port=2002;urp;StarOffice.ComponentContext"])
    local = uno.getComponentContext()
    resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
    context = None
    for attempt in range(50):
        try:
            context = resolver.resolve("uno:socket,host=127.0.0.1,port=2002;urp;StarOffice.ComponentContext")
            break
        except NoConnectException:
            time.sleep(0.1)
    if context is None:
        raise RuntimeError("LibreOffice object model is unavailable")
    desktop = context.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", context)
    document = desktop.loadComponentFromURL("private:factory/scalc", "_blank", 0, ())
    document.Sheets.getByIndex(0).getCellRangeByName("A1").String = "Synthetic saved cell"
    property_value = uno.createUnoStruct("com.sun.star.beans.PropertyValue")
    property_value.Name = "FilterName"
    property_value.Value = "Calc MS Excel 2007 XML"
    output = ROOT / "actual.xlsx"
    document.storeAsURL(output.as_uri(), (property_value,))
    document.close(True)
    with zipfile.ZipFile(output) as stream:
        if b"Synthetic saved cell" not in stream.read("xl/sharedStrings.xml"):
            raise RuntimeError("Persisted spreadsheet cell is incorrect")
    return "LibreOffice Calc saved XLSX through UNO; independently read persisted cell"


def fingerprints():
    result = {}
    for executable in ("converseen", "xarchiver", "mousepad", "libreoffice", "pdftotext"):
        path = Path(shutil.which(executable))
        version = subprocess.check_output(["dpkg-query", "-W", "-f=${Version}",
                                           "libreoffice-calc" if executable == "libreoffice" else
                                           "poppler-utils" if executable == "pdftotext" else executable], timeout=5).decode()
        result[executable] = {"version": version, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    for executable in ("k-lani-bot-node", "k-lani-bot-cartridge-lab"):
        result[executable] = {"sha256": hashlib.sha256((Path("/app") / executable).read_bytes()).hexdigest()}
    result["packages_sha256"] = hashlib.sha256(Path("/usr/share/k-lani-bot-performance-packages.tsv").read_bytes()).hexdigest()
    return result



def main():
    report = {"fingerprints": fingerprints(), "checks": {}}
    try:
        for name, probe in (("webp", webp), ("zip", archive), ("text_pdf", text_pdf), ("spreadsheet", spreadsheet)):
            try:
                report["checks"][name] = {"status": "passed", "detail": probe()}
            except Exception as exception:
                report["checks"][name] = {"status": "unavailable", "detail": type(exception).__name__ + ": " + str(exception)}
    finally:
        for process in PROCESSES:
            if process.poll() is None:
                import signal
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
    print(json.dumps(report))
    return 0 if all(check["status"] == "passed" for check in report["checks"].values()) else 3


if __name__ == "__main__":
    raise SystemExit(main())
