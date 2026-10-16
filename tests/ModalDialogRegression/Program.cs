using System.Diagnostics;
using KLaniBot;
using KLaniBot.Guard;
using KLaniBot.Platforms;
using KLaniBot.Platforms.Linux;

// Runs the real Linux accessibility backend against a real GTK window with a modal dialog, inside a
// private virtual display. The review of 2026-10-01 found that a semantic click on the main window was
// executed while a modal dialog was open; this test keeps that closed.
//
// Usage: ModalDialogRegression <path to k-lani-bot-cartridge-lab.dll>
PrivateGuiSession.Require();
string fixturePath = Path.GetFullPath(args[0]);
const string ImportButton = "Import 3MF/STL/STEP/SVG/OBJ/AMF...";
const string BlockingDialogButton = "Open archive dialog...";
const string DismissButton = "Dismiss";
string BehindModalText = ModalDialogPolicy.StopBehindModalMessage;
string ModalTargetText = ModalDialogPolicy.StopForModalTargetMessage;
int passed = 0;

void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception("Modal dialog: " + message);
    }
    passed++;
}

var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
start.ArgumentList.Add(fixturePath);
start.ArgumentList.Add("fixture");
start.ArgumentList.Add("model-editor");
using var fixture = Process.Start(start)!;
var ui = new LinuxUiService();

IEnumerable<VisualTreeNode> Flatten(VisualTreeNode node)
{
    yield return node;
    foreach (var child in node.Children)
    {
        foreach (var inner in Flatten(child))
        {
            yield return inner;
        }
    }
}

// Every observation replaces the element ids, so ids must be used straight after the call that returned them.
VisualTreeNode? Find(string name)
{
    var tree = ui.GetVisualTree(fixture.Id, null, 8) as VisualTreeNode;
    return tree == null ? null : Flatten(tree).FirstOrDefault(node => node.Name == name);
}

VisualTreeNode WaitFor(string name)
{
    for (int attempt = 0; attempt < 100; attempt++)
    {
        try
        {
            var found = Find(name);
            if (found != null)
            {
                return found;
            }
        }
        catch (InvalidOperationException)
        {
            // The window is still being created.
        }
        Thread.Sleep(100);
    }
    throw new Exception("Modal dialog: '" + name + "' never appeared.");
}

void WaitUntilGone(string name)
{
    for (int attempt = 0; attempt < 100; attempt++)
    {
        if (Find(name) == null)
        {
            return;
        }
        Thread.Sleep(100);
    }
    throw new Exception("Modal dialog: '" + name + "' never went away.");
}

// What the backend finds for the observed element with this name. The element is observed first, so its number is current.
ModalDialogDetection Detection(string name)
{
    var element = Find(name) ?? throw new Exception("Modal dialog: '" + name + "' not found.");
    return ui.DetectModalDialog(element.Id);
}

// Returns null when the action is allowed, otherwise the refusal message.
string? Refusal(string name, ModalDialogMode dialogs, bool decisionRecorded)
{
    var element = Find(name) ?? throw new Exception("Modal dialog: '" + name + "' not found.");
    try
    {
        ui.PrepareElementInteraction(element.Id, WindowInteractionMode.BackgroundSemantic, dialogs, decisionRecorded);
        return null;
    }
    catch (InvalidOperationException exception)
    {
        return exception.Message;
    }
}

string StatusText()
{
    var tree = ui.GetVisualTree(fixture.Id, null, 8) as VisualTreeNode;
    return Flatten(tree!).First(node => node.Name == "Fixture result").Value ?? "";
}

try
{
    WaitFor("Print plate");
    Check(Refusal("Print plate", ModalDialogMode.Pause, false) == null, "a window without a dialog is not blocked");
    Check(Detection("Print plate") == ModalDialogDetection.Absent, "a window without a dialog is detected as having none");
    string statusBefore = StatusText();

    Check(ui.ShowingModalWindowTitle(fixture.Id) == null, "no modal window of the program shows before the import");
    var import = WaitFor(ImportButton);
    var (opened, _) = ui.InvokeBackgroundElement(import.Id);
    Check(opened, "the import button opens the file picker");
    WaitFor("Cancel");
    Check(!string.IsNullOrEmpty(ui.ShowingModalWindowTitle(fixture.Id)), "the open file picker is seen as the program's modal window");
    Check(Detection("Print plate") == ModalDialogDetection.Present, "the window behind the open file picker is detected as blocked");
    Check(Detection("Cancel") == ModalDialogDetection.Present, "an element inside the open file picker is detected as the dialog");

    string? paused = Refusal("Print plate", ModalDialogMode.Pause, false);
    Check(paused != null && paused.Contains(BehindModalText), "the main window behind the dialog is refused while dialogs are paused");
    string? decided = Refusal("Print plate", ModalDialogMode.AllowInteraction, true);
    Check(
        decided != null && decided.Contains(BehindModalText),
        "the main window behind the dialog is refused although dialogs are allowed and a decision was recorded");
    Check(StatusText() == statusBefore, "nothing in the blocked window ran");

    string? dialogPaused = Refusal("Cancel", ModalDialogMode.Pause, false);
    Check(
        dialogPaused != null && dialogPaused.Contains(ModalTargetText) && !dialogPaused.Contains(BehindModalText),
        "an element in the dialog waits while dialogs are paused");
    string? dialogWithoutDecision = Refusal("Cancel", ModalDialogMode.AllowInteraction, false);
    Check(
        dialogWithoutDecision != null && dialogWithoutDecision.Contains(ModalTargetText),
        "an element in the dialog needs the recorded decision");
    Check(
        Refusal("Cancel", ModalDialogMode.AllowInteraction, true) == null,
        "an element in the dialog is allowed with dialogs enabled and a decision");

    var cancel = WaitFor("Cancel");
    string? dialogText = ui.ReadWindowText(cancel.Id);
    Check(dialogText != null && dialogText.Contains("Cancel", StringComparison.Ordinal), "the dialog's text is read through its element");
    // The same element number must still work: reading the text is no new observation.
    ui.PrepareElementInteraction(cancel.Id, WindowInteractionMode.BackgroundSemantic, ModalDialogMode.AllowInteraction, true);
    Check(true, "reading the dialog's text keeps its element numbers valid");
    var (closed, _) = ui.InvokeBackgroundElement(cancel.Id);
    Check(closed, "the dialog is closed through its own button");
    WaitUntilGone("Cancel");
    Check(ui.ShowingModalWindowTitle(fixture.Id) == null, "no modal window shows once the dialog is closed");
    Check(Refusal("Print plate", ModalDialogMode.Pause, false) == null, "the main window works again once the dialog is closed");
    Check(Detection("Print plate") == ModalDialogDetection.Absent, "no dialog is detected once the file picker is closed");

    // A GTK3 click handler that runs its dialog with gtk_dialog_run keeps the connection that carried the action busy
    // until the dialog closes. The node must still read the application, or the dialog can never be seen or closed.
    var blockingOpener = WaitFor(BlockingDialogButton);
    var (blockingOpened, _) = ui.InvokeBackgroundElement(blockingOpener.Id);
    Check(blockingOpened, "the button whose handler runs its own dialog loop is clicked semantically");
    var dismiss = WaitFor(DismissButton);
    Check(dismiss.IsEnabled, "the dialog of a handler that is still running is observed after the click");
    Check(!string.IsNullOrEmpty(ui.ShowingModalWindowTitle(fixture.Id)), "that dialog is seen as the program's modal window");
    string? behindBlocking = Refusal("Print plate", ModalDialogMode.Pause, false);
    Check(
        behindBlocking != null && behindBlocking.Contains(BehindModalText),
        "the main window behind that dialog is refused as well");

    // A key event makes the accessibility bridge wait inside a nested loop. If the connection that carried the click had
    // been closed meanwhile, the application would deadlock here and nothing could be observed again.
    Check(ui.KeyChord(["escape"]).Success, "a key reaches the dialog that runs inside the click handler");
    WaitUntilGone(DismissButton);
    Check(StatusText() == "Blocking dialog dismissed", "the handler returned after the key closed its dialog");

    var reopened = ui.InvokeBackgroundElement(WaitFor(BlockingDialogButton).Id);
    Check(reopened.Success, "the same dialog is opened a second time through a new connection");
    var (dismissed, _) = ui.InvokeBackgroundElement(WaitFor(DismissButton).Id);
    Check(dismissed, "the dialog is closed through its own button");
    WaitUntilGone(DismissButton);
    Check(Refusal("Print plate", ModalDialogMode.Pause, false) == null, "the main window works again after the dialog loop ended");
    var editor = WaitFor("Edit process profile");
    var bounds = System.Text.Json.JsonSerializer.SerializeToNode(editor.Bounds)!;
    int pointerX = bounds["x"]!.GetValue<int>() + bounds["width"]!.GetValue<int>() / 2;
    int pointerY = bounds["y"]!.GetValue<int>() + bounds["height"]!.GetValue<int>() / 2;
    Check(ui.MouseMove(pointerX, pointerY).Success, "a pointer move reaches the freshly observed fixture control");
    var repeated = Stopwatch.StartNew();
    Check(ui.MouseMove(pointerX, pointerY).Success && repeated.Elapsed < TimeSpan.FromSeconds(1),
        "moving to the existing pointer position does not wait for a motion event or time out");
    var entry = WaitFor("Process profile edit");
    Check(ui.SetText(entry.Id, "keyboard").Success, "the private entry receives a keyboard probe value");
    // The element's focus interface must route keyboard input without a coordinate click.
    Check(ui.ClickElement(entry.Id).Success, "the observed entry receives semantic keyboard focus");
    Check(ui.KeyChord(["end"]).Success && ui.KeyChord(["space"]).Success,
        "End and Space are dispatched through the physical keyboard backend");
    Check(WaitFor("Process profile edit").Value == "keyboard ", "End places the inserted space after the existing text");
    Check(ui.KeyChord(["home"]).Success && ui.KeyChord(["space"]).Success,
        "Home moves the insertion point to the start of the entry");
    Check(WaitFor("Process profile edit").Value == " keyboard ", "Home places the inserted space before the existing text");
    Check(ui.KeyChord(["pagedown"]).Success && ui.KeyChord(["pageup"]).Success,
        "page navigation keys are accepted by the real X11 input backend");
}
finally
{
    try
    {
        fixture.Kill(true);
    }
    catch (InvalidOperationException)
    {
        // The fixture had already exited.
    }
}
Console.WriteLine($"{passed} modal dialog regression checks passed.");
