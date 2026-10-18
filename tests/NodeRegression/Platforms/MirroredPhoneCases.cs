using KLaniBot.Platforms;
using KLaniBot.Platforms.Mirroring;

// The mirrored-phone backend without a phone: a fake screen recognizes fixed text, the window moves between observation and tap,
// text disappears, the window pauses. Taps must land on the text where it is now, never on a stale or vanished place.
internal static class MirroredPhoneCases
{
    public static int Run()
    {
        var checks = new Checks("Mirrored phone");
        CheckObservation(checks);
        CheckFreshTap(checks);
        CheckRefusals(checks);
        CheckTypingAndChecks(checks);

        return checks.Passed;
    }

    private static void CheckObservation(Checks checks)
    {
        var screen = new FakeMirroredScreen();
        var phone = new MirroredPhoneUiService(screen, "test-mirroring");

        var root = (VisualTreeNode)phone.GetVisualTree(null, null)!;
        checks.That(root.Children.Select(node => node.Name).SequenceEqual(["Notes", "New note", "Notes"]),
            "the recognized lines are the elements, ordered from the top");
        checks.That(root.Children.All(node => node.Role == MirroredPhoneUiService.RecognizedTextRole),
            "every element says that it is recognized text");
        object bounds = root.Children[1].Bounds!;
        checks.That(bounds.ToString()!.Contains("x = 138") && bounds.ToString()!.Contains("y = 275"),
            "an element's bounds are host points of the window");
        checks.That(phone.ListWindows().Single().Handle == new IntPtr(FakeMirroredScreen.WindowId), "the one window is the mirror");
    }

    private static void CheckFreshTap(Checks checks)
    {
        var screen = new FakeMirroredScreen();
        var phone = new MirroredPhoneUiService(screen, "test-mirroring");
        var root = (VisualTreeNode)phone.GetVisualTree(null, null)!;
        int newNote = root.Children[1].Id;

        screen.MoveWindowBy(40, 25);
        ClickOutcome tapped = phone.MouseClick(newNote);
        checks.That(tapped.Success && screen.Taps.Single() == new ScreenPoint(215, 318.75),
            "a tap after the window moved goes to the text in its new place");
        int lowerNotes = root.Children[2].Id;
        phone.MouseClick(lowerNotes);
        checks.That(screen.Taps[1] == new ScreenPoint(252.5, 656.25), "the second of two equal texts is tapped, not the first");
        checks.That(screen.Reads == 3, "every tap reads the window again");
    }

    private static void CheckRefusals(Checks checks)
    {
        var screen = new FakeMirroredScreen();
        var phone = new MirroredPhoneUiService(screen, "test-mirroring");
        var root = (VisualTreeNode)phone.GetVisualTree(null, null)!;

        screen.Remove("New note");
        ClickOutcome gone = phone.MouseClick(root.Children[1].Id);
        bool namesVisible = gone.Message.Contains("no longer visible") && gone.Message.Contains("'Notes'");
        checks.That(!gone.Success && namesVisible && screen.Taps.Count == 0,
            "vanished text is not tapped and the reply names what is visible");
        checks.That(!phone.MouseClick(0, "left", 10, 10).Success && screen.Taps.Count == 0, "a point outside the window is not tapped");
        checks.That(!phone.MouseClick(root.Children[0].Id, "right").Success, "a right button is refused");
        screen.Pause();
        try
        {
            phone.MouseClick(root.Children[0].Id);
            checks.That(false, "a paused mirror must not take a tap");
        }
        catch (InvalidOperationException)
        {
            checks.Reached();
        }
        checks.That(!phone.Screenshot().Success && screen.Taps.Count == 0, "a paused mirror has no screenshot and got no input");
    }

    private static void CheckTypingAndChecks(Checks checks)
    {
        var screen = new FakeMirroredScreen();
        var phone = new MirroredPhoneUiService(screen, "test-mirroring");
        var root = (VisualTreeNode)phone.GetVisualTree(null, null)!;
        checks.That(phone.GetDisplayedTarget(root.Children[1].Id)?.Name == "New note" && ((IUiService)phone).GetTargetIdentity(1) == null,
            "the panel names the text while the guard gets no identity");

        UiOutcome typed = phone.TypeText(root.Children[1].Id, "Einkauf", false, true, 0);
        checks.That(typed.Success && screen.Taps.Count == 1 && screen.Typed.Single() == "Einkauf" && screen.Pressed.Single() == "enter",
            "typing taps the field, types the text and presses enter");
        checks.That(!phone.TypeText(root.Children[1].Id, "x", true, false, 0).Success, "clearing a field is refused, not guessed");
        checks.That(phone.VerifyElement(null, "New note", null, null, null, null, "visible", null, 0).Success,
            "visible text is verified");
        checks.That(!phone.VerifyElement(null, "Missing", null, null, null, null, "exists", null, 0).Success,
            "missing text is not verified");
        checks.That(phone.VerifyElement(null, "Missing", null, null, null, null, "absent", null, 0).Success, "absence is verified");
        checks.That(phone.Screenshot().Png!.SequenceEqual(FakeMirroredScreen.Image), "a screenshot is only the mirroring window");
    }
}

// A mirroring window at (100, 200), 300 by 600 points, showing three lines at binary-exact positions so taps compare exactly;
// it can move, lose a line or pause.
internal sealed class FakeMirroredScreen : IMirroredScreen
{
    public const long WindowId = 77;
    public static readonly byte[] Image = [137, 80, 78, 71];
    private readonly List<RecognizedLine> _lines =
    [
        new("Notes", 0.99, 0.125, 0.03125, 0.25, 0.0625),
        new("New note", 0.98, 0.125, 0.125, 0.25, 0.0625),
        new("Notes", 0.97, 0.25, 0.6875, 0.25, 0.0625)
    ];
    private double _x = 100;
    private double _y = 200;
    private bool _paused;

    public List<ScreenPoint> Taps { get; } = [];

    public List<string> Typed { get; } = [];

    public List<string> Pressed { get; } = [];

    public int Reads
    {
        get;
        private set;
    }

    public void MoveWindowBy(double x, double y)
    {
        _x += x;
        _y += y;
    }

    public void Remove(string text)
    {
        _lines.RemoveAll(line => line.Text == text);
    }

    public void Pause()
    {
        _paused = true;
    }

    public MirroredWindow? FindWindow()
    {
        return _paused ? null : new MirroredWindow(WindowId, 4242, _x, _y, 300, 600);
    }

    public IReadOnlyList<RecognizedLine> Read(MirroredWindow window)
    {
        Reads++;
        return _lines.ToArray();
    }

    public byte[] Capture(MirroredWindow window)
    {
        return Image;
    }

    public UiOutcome Tap(MirroredWindow window, ScreenPoint point)
    {
        Taps.Add(point);
        return new(true, "tapped");
    }

    public UiOutcome Type(MirroredWindow window, string text)
    {
        Typed.Add(text);
        return new(true, "typed");
    }

    public UiOutcome Keys(MirroredWindow window, string[] keys)
    {
        Pressed.Add(string.Join("+", keys));
        return new(true, "pressed");
    }
}
