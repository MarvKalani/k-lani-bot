using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Visibility;

int passed = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); passed++; }
var clock = new FakeClock();
var display = new Display();
var audit = new List<JsonObject>();
using var gate = new AccessVisibility(new(), display, e => audit.Add(e), clock);
var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
Check(gate.State.Status == VisibilityStatus.Idle && gate.State.ScreenBorder && !gate.State.Unattended, "Unsafe defaults.");
gate.Touch(caller);
Check(display.Last?.Status == VisibilityStatus.Connected, "Connection is not visible.");
var blocked = gate.Begin(Tool.TakeScreenshot, caller);
Check(blocked.Activity == null && blocked.Rejection?["code"]?.ToString() == "consent_required", "Screen read before consent.");
var pending = gate.State.Prompt!;
Check(gate.State.Prompt != null, "No local question.");
Check(!gate.Decide(pending.Key, Guid.CreateVersion7().ToString(), SessionConsent.Allowed), "Stale/foreign challenge approved.");
Check(gate.Decide(pending.Key, pending.Challenge, SessionConsent.Deferred), "Deferral failed.");
Check(gate.Begin(Tool.GetUiTree, caller).Rejection?["code"]?.ToString() == "consent_deferred", "Deferred read dispatched.");
clock.Advance(299);
gate.Touch(caller);
Check(gate.State.Prompt == null, "Prompt came before five minutes.");
clock.Advance(1);
gate.Touch(caller);
Check(gate.State.Prompt?.Challenge != pending.Challenge, "Five-minute reminder missing/stale.");
pending = gate.State.Prompt!;
Check(gate.Decide(pending.Key, pending.Challenge, SessionConsent.Allowed), "Local approval failed.");
Check(gate.State.Border, "Allowed connected session must remain marked before an action.");
var presentation = SessionPresentation.Create("Test client", "Synthetic editor test", "master-fixture", "node-fixture", "observe, ui");
gate.SetSessionPresentation(caller, presentation);
Check(gate.State.Presentation?.Master == "master-fixture", "Master identity missing from the session display.");
var action = gate.Begin(Tool.TakeScreenshot, caller);
Check(
    action.Rejection == null && display.Last?.Status == VisibilityStatus.Active && display.Last.Border,
    "Border not acknowledged before action.");
var second = new RequestContext("operator", Guid.CreateVersion7().ToString());
gate.SetSessionPresentation(second, SessionPresentation.Create("Other client", "Different assignment", "other-master"));
gate.Begin(Tool.TypeText, second);
var other = gate.State.Prompt!;
Check(gate.State.Presentation?.Client == "Test client" && other.Presentation?.Client == "Other client",
    "Pending client replaced the identity of the executing client on the activity indicator.");
Check(!gate.Decide(other.Key, other.Challenge, SessionConsent.Allowed), "Approval possible during injected UI action.");
gate.SetUnattended(true);
Check(!gate.State.Unattended, "Unattended mode changed during active action.");
gate.SetWindowInteraction(WindowInteractionMode.BackgroundSemantic, ModalDialogMode.AllowInteraction);
Check(gate.State.WindowInteraction == WindowInteractionMode.Foreground && gate.State.ModalDialogs == ModalDialogMode.Pause,
    "Window/dialog policy changed during injected input.");
gate.SetScreenshotRecording(true);
Check(!gate.State.RecordActionScreenshots, "Screenshot policy changed during injected input.");
gate.SetBorder(false);
Check(gate.State.ScreenBorder, "Border disabled during active action.");
gate.RevokeAll();
Check(gate.Begin(Tool.TypeText, caller).Rejection?["code"]?.ToString() == "consent_denied", "Revoked session still controls UI.");
bool revoked = false;
try { gate.RequireApproval(caller); } catch (UnauthorizedAccessException) { revoked = true; }
Check(revoked, "Macro step bypasses local revocation.");
action.Activity!.Dispose();
Check(gate.State.Status == VisibilityStatus.Active, "Short activity invisible.");
clock.Advance(1);
gate.Refresh();
Check(!gate.State.Border && gate.State.Status == VisibilityStatus.Connected, "Border never cleared.");
gate.RequestApprovalLocally();
pending = gate.State.Prompt!;
Check(pending != null && gate.Decide(pending.Key, pending.Challenge, SessionConsent.Allowed), "Denied session cannot be reopened locally.");
gate.SetUnattended(true);
Check(gate.State.Unattended, "Explicit test-machine mode unavailable.");
var unattended = gate.Begin(Tool.RunMacro, caller);
Check(unattended.Rejection == null && display.Last?.Border == true, "Macro can bypass visual border.");
unattended.Activity?.Dispose();
clock.Advance(1);
gate.Refresh();
gate.SetBorder(false);
var withoutBorder = gate.Begin(Tool.TakeScreenshot, caller);
Check(
    withoutBorder.Rejection == null && display.Last?.Status == VisibilityStatus.Active && display.Last.Border == false,
    "Opt-out disables activity/consent.");
withoutBorder.Activity?.Dispose();
clock.Advance(1);
gate.Refresh();
gate.Touch(caller, false);
gate.Touch(second, false);
Check(gate.State.Status == VisibilityStatus.Idle, "Disconnect not visible.");
var quiet = gate.Begin(Tool.GetMachineCapabilities, caller);
Check(quiet.Rejection == null && !gate.State.Border && gate.State.Prompt == null, "Discovery requires screen access/consent.");
clock.Advance(76);
gate.Refresh();
Check(gate.State.Connections == 0, "Dead session never expires.");
Check(
    audit.Any(x => x["event"]?.ToString() == "consent_reprompted")
        && audit.Any(x => x["event"]?.ToString() == "all_sessions_revoked_locally"),
    "Consent audit incomplete.");
Check(audit.All(x => Guid.Parse(x["request_id"]!.ToString()).Version == 7), "Visibility event identifiers are not UUIDv7.");
var failure = new Display { FailActive = true };
using var failedGate = new AccessVisibility(new() { ConsentMode = ConsentMode.Unattended }, failure, _ => { }, clock);
Check(
    failedGate.Begin(Tool.TakeScreenshot, caller).Rejection?["code"]?.ToString() == "indicator_unavailable",
    "Indicator failure dispatches screen read.");
Check(failedGate.Begin(Tool.TypeText, caller).Rejection != null, "Failed indicator is bypassable on next action.");
Check(
    AccessVisibility.NeedsBorder(Tool.FindElements)
        && AccessVisibility.NeedsBorder(Tool.Hotkey)
        && AccessVisibility.NeedsBorder(Tool.GetUiDelta),
    "Sensitive tool classification incomplete.");
Check(!AccessVisibility.NeedsBorder(Tool.FsReadFile) && AccessVisibility.NeedsConsent(Tool.FsReadFile), "File access lacks consent.");
using var detailsGate = new AccessVisibility(new(), new Display(), _ => { }, clock);
detailsGate.SetSessionPresentation(caller, presentation);
Check(detailsGate.State.Presentation?.Master == "master-fixture", "Master identity is hidden before approval.");
detailsGate.Begin(Tool.TypeText, caller);
var detailsPrompt = detailsGate.State.Prompt!;
Check(detailsGate.Decide(detailsPrompt.Key, detailsPrompt.Challenge, SessionConsent.Allowed), "Details fixture approval failed.");
detailsGate.SetSessionPresentation(caller, presentation with { Purpose = "Another assignment" });
Check(
    detailsGate.Begin(Tool.TypeText, caller).Rejection?["code"]?.ToString() == "consent_required",
    "Changed assignment reused old consent.");
Check(detailsGate.State.Prompt?.Challenge != detailsPrompt.Challenge, "Changed assignment retained the old consent challenge.");
SessionPresentation clientClaim = SessionPresentation.Decode(presentation.Encode());
Check(clientClaim.Master != presentation.Master && clientClaim.Rights != presentation.Rights && clientClaim.Node != presentation.Node,
    "Client-controlled labels can impersonate trusted master identity or rights.");
VisibilityOptions? saved = null;
using var preferenceGate = new AccessVisibility(new(), new Display(), _ => { }, clock, value => saved = value);
preferenceGate.SetWindowInteraction(WindowInteractionMode.BackgroundSemantic, ModalDialogMode.AllowInteraction);
preferenceGate.SetScreenshotRecording(true);
preferenceGate.SetBorder(false);
preferenceGate.SetUnattended(true);
Check(saved?.WindowInteraction == WindowInteractionMode.BackgroundSemantic && saved.ModalDialogs == ModalDialogMode.AllowInteraction
    && saved.RecordActionScreenshots
        && !saved.ScreenBorder
        && saved.ConsentMode == ConsentMode.Unattended, "Preference setters lost window/dialog/trace choices.");
var roundtrip = System.Text.Json.JsonSerializer.Deserialize<VisibilityOptions>(System.Text.Json.JsonSerializer.Serialize(saved))!;
roundtrip.Validate();
Check(roundtrip.WindowInteraction == WindowInteractionMode.BackgroundSemantic, "Named enum preference failed serialization.");
// The private-session guard decides whether a GUI test may create windows at all.
// An empty value removes the variable instead of setting it.
Func<string, string?> PrivateEnvironment(string overriddenName = "", string overriddenValue = "")
{
    var variables = new Dictionary<string, string>
    {
        ["K_LANI_BOT_PRIVATE_GUI_TEST"] = "1",
        ["GDK_BACKEND"] = "x11",
        ["DISPLAY"] = ":99",
        ["K_LANI_BOT_PRIVATE_DISPLAY"] = ":99",
        ["XDG_RUNTIME_DIR"] = "/tmp/private-runtime",
        ["K_LANI_BOT_PRIVATE_RUNTIME"] = "/tmp/private-runtime"
    };
    if (overriddenName.Length > 0 && overriddenValue.Length == 0)
    {
        variables.Remove(overriddenName);
    }
    else if (overriddenName.Length > 0)
    {
        variables[overriddenName] = overriddenValue;
    }
    return name => variables.GetValueOrDefault(name);
}
Check(PrivateGuiSession.FindViolation(PrivateEnvironment()) == null, "A fully private session was refused.");
Check(PrivateGuiSession.FindViolation(PrivateEnvironment("K_LANI_BOT_PRIVATE_GUI_TEST", "")) != null, "Missing guard flag accepted.");
Check(PrivateGuiSession.FindViolation(PrivateEnvironment("GDK_BACKEND", "wayland")) != null, "Inherited Wayland backend accepted.");
Check(PrivateGuiSession.FindViolation(PrivateEnvironment("GDK_BACKEND", "")) != null, "Unpinned GTK backend accepted.");
Check(PrivateGuiSession.FindViolation(PrivateEnvironment("WAYLAND_DISPLAY", "wayland-0")) != null, "Wayland display variable accepted.");
Check(PrivateGuiSession.FindViolation(PrivateEnvironment("DISPLAY", ":0")) != null, "Display that this run did not create accepted.");
Check(
    PrivateGuiSession.FindViolation(PrivateEnvironment("K_LANI_BOT_PRIVATE_DISPLAY", "")) != null,
    "Unrecorded private display accepted.");
Check(
    PrivateGuiSession.FindViolation(PrivateEnvironment("XDG_RUNTIME_DIR", "/run/user/1000")) != null,
    "Real runtime directory accepted.");
Check(PrivateGuiSession.FindViolation(PrivateEnvironment("XDG_RUNTIME_DIR", "")) != null, "Missing runtime directory accepted.");
passed += ConsentCases.Run();
Console.WriteLine($"{passed} visibility/consent regression checks passed.");
// Records the last frame of the gate and can fail when the active frame is drawn, to test a broken indicator.
sealed class Display : IVisibilityDisplay
{
    public VisibilityState? Last; public bool FailActive; public void Render(VisibilityState state)
    {
        if (FailActive && state.Status == VisibilityStatus.Active)
            throw new Exception("GUI failed");
        Last = state;
    }
    public void Dispose()
    {
    }
}
// Time in whole seconds that the cases advance, so reminders and deferrals happen without waiting.
sealed class FakeClock : TimeProvider
{
    private long _millis; public override long TimestampFrequency
        => 1000; public override long GetTimestamp()
            => _millis; public override DateTimeOffset GetUtcNow()
                => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(_millis); public void Advance(int seconds)
                    => _millis += seconds * 1000L;
}
