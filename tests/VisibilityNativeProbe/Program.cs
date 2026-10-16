using KLaniBot;
using KLaniBot.Localization;
using KLaniBot.Visibility;

string outputDirectory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outputDirectory);

if (OperatingSystem.IsMacOS())
{
    // Started by launchd like the node, so this is the language the node falls back to without uiLanguage.
    Console.WriteLine("UI culture: " + System.Globalization.CultureInfo.CurrentUICulture.Name + " -> " + UiTexts.SystemLanguage());
    MacVisibilityHost.TestAssignmentOffscreen(outputDirectory);
    return 0;
}
if (!OperatingSystem.IsLinux())
{
    throw new InvalidOperationException("The native GUI probe runs on Linux or macOS only.");
}
PrivateGuiSession.Require();
CheckEscapeKeyWatch();

using var host = new LinuxVisibilityHost();
return host.Run(async () =>
{
    // GTK has opened its display by now. The environment check above cannot see which backend GTK
    // actually chose, so compare the real display name before the first window is created.
    host.TestRequirePrivateDisplay();
    var details = SessionPresentation.Create(
        "Native test client",
        "Synthetisches Dokument prüfen",
        "master-fixture",
        "node-fixture",
        "observe, ui");
    host.Render(new VisibilityState(VisibilityStatus.Connected, 1, true, null, false, true, details));
    host.TestAssignment(outputDirectory, "granted", UiTexts.Get(UiText.AssignmentGranted));
    host.Render(new VisibilityState(VisibilityStatus.Active, 1, true, null, false, true, details));
    host.TestAssignment(outputDirectory, "active", UiTexts.Get(UiText.AssignmentWorking));
    var changed = details with
    {
        Client = "Another test client",
        Purpose = "Zweiter synthetischer Auftrag"
    };
    host.Render(new VisibilityState(VisibilityStatus.Active, 1, true, null, false, true, changed));
    host.TestAssignment(outputDirectory, "changed", "Zweiter synthetischer Auftrag");
    host.Render(new VisibilityState(VisibilityStatus.Connected, 1, false, null, false, false, changed));
    host.TestNoActivityWindows();
    host.Render(new VisibilityState(VisibilityStatus.Active, 1, true, null, false, true, details));
    host.TestAssignment(outputDirectory, "restored", "Native test client");
    CheckStepList(host, details, outputDirectory);
    host.Render(new VisibilityState(VisibilityStatus.Idle, 0, false, null, false, true));
    host.TestNoActivityWindows();
    ScreenRectangle[] twoMonitors = [new(640, 0, 640, 720), new(0, 0, 640, 720)];
    host.TestUseMonitors(twoMonitors);
    host.Render(new VisibilityState(VisibilityStatus.Active, 1, true, null, false, true, details));
    host.TestBorderOnEveryMonitor(twoMonitors);
    host.TestAssignment(outputDirectory, "two-monitors", "Native test client");
    host.Render(new VisibilityState(VisibilityStatus.Idle, 0, false, null, false, true));
    host.TestNoActivityWindows();
    await Task.CompletedTask;
    Console.WriteLine(
        "Linux native assignment checks passed: granted/active text, changed client/task, native input pass-through, window geometry, "
        + "border on every monitor, "
        + "opt-out and cleanup. Private Xvfb PNGs recorded.");
    return 0;
});

// A virtual display has no physical keyboard, so the watch that counts sent keys proves that ESC arrives, and the real watch
// proves that ESC sent through xdotool, the way the node types, never counts.
static void CheckEscapeKeyWatch()
{
    if (!X11EscapeKeyWatch.TryStart(false, out X11EscapeKeyWatch? physicalOnly))
    {
        throw new InvalidOperationException("The private X server offers no XInput 2.");
    }
    using (physicalOnly)
    {
        SendKey("Escape");
        if (SeesEscape(physicalOnly))
        {
            throw new InvalidOperationException("An ESC the node sends would end its own session.");
        }
    }
    if (!X11EscapeKeyWatch.TryStart(true, out X11EscapeKeyWatch? everyKey))
    {
        throw new InvalidOperationException("The private X server offers no XInput 2.");
    }
    using (everyKey)
    {
        SendKey("a");
        if (SeesEscape(everyKey))
        {
            throw new InvalidOperationException("Another key counted as ESC.");
        }
        SendKey("Escape");
        if (!SeesEscape(everyKey))
        {
            throw new InvalidOperationException("The key watch never saw ESC.");
        }
    }
    Console.WriteLine("Escape key watch checks passed: ESC arrives through XInput 2, other keys and keys the node sends do not count.");
}

static void SendKey(string key)
{
    using var xdotool = System.Diagnostics.Process.Start("xdotool", ["key", key])!;
    xdotool.WaitForExit();
}

static bool SeesEscape(X11EscapeKeyWatch watch)
{
    for (int attempt = 0; attempt < 20; attempt++)
    {
        if (watch.TakePhysicalEscape())
        {
            return true;
        }
        Thread.Sleep(50);
    }
    return false;
}

// While the agent is paused, the banner grows by the pace line and the step list; the agent's plan is marked as its claim.
static void CheckStepList(LinuxVisibilityHost host, SessionPresentation details, string outputDirectory)
{
    int normalHeight = host.TestAssignmentHeight();
    var steps = new StepList(
        [new StepView(StepKind.Click, "Anmelden")],
        new StepView(StepKind.Type, "Kennwort, 12 Zeichen"),
        [new StepView(StepKind.Announced, "Posteingang öffnen")],
        true);
    var paused = new VisibilityState(VisibilityStatus.Active, 1, true, null, false, true, details, Pace: Pace.Paused, Steps: steps);
    host.Render(paused);
    host.TestAssignment(outputDirectory, "paused", UiTexts.Get(UiText.AssignmentPaused));
    host.TestAssignment(outputDirectory, "paused-plan", UiTexts.Format(UiText.StepNextFromAgent, "Posteingang öffnen"));
    if (host.TestAssignmentHeight() <= normalHeight)
    {
        throw new InvalidOperationException("The banner did not grow for the step list.");
    }
    host.Render(paused with
    {
        Pace = Pace.SlowMotion
    });
    string typingNow = UiTexts.Format(UiText.StepNow, UiTexts.Get(UiText.StepType) + ": Kennwort, 12 Zeichen");
    host.TestAssignment(outputDirectory, "slow-motion", typingNow);
    host.Render(paused with
    {
        Pace = Pace.Normal
    });
    Console.WriteLine("Step list checks passed: pause and slow motion show the pace, the steps and the agent's plan; the banner grows.");
}
