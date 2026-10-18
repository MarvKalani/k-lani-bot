namespace KLaniBot.Platforms.Mirroring;

// Input into the mirrored phone: a tap reads the window and its text afresh and goes to the same text where it is now, typing
// follows a tap on its field, keys go to the mirroring window, and anything a mirrored phone cannot do is refused by name.
public sealed partial class MirroredPhoneUiService
{
    private const int VisibleTextsNamed = 20;
    // Same text further than this from where it was observed (a share of the window) is another occurrence, not this one.
    private const double SameTextDistance = 0.08;

    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null)
    {
        if (button != "left")
        {
            return new(false, "A mirrored phone takes taps, not the " + button + " button.", null);
        }
        if (id > 0)
        {
            return TapFresh(id);
        }

        return x.HasValue && y.HasValue ? TapPoint(x.Value, y.Value) : new(false, "Name a recognized text or a point.", null);
    }

    // Throws InvalidOperationException when the mirroring window is not visible or cannot be read.
    private ClickOutcome TapFresh(int id)
    {
        if (!_cache.TryGetValue(id, out ObservedText? observed))
        {
            return new(false, "Stale target; observe the mirrored phone again.", null);
        }

        MirroredWindow window = RequireWindow();
        IReadOnlyList<RecognizedLine> lines = screen.Read(window);
        RecognizedLine? current = FreshMatch(observed, lines);
        if (current == null)
        {
            string visible = string.Join(", ", lines.Take(VisibleTextsNamed).Select(line => "'" + line.Text + "'"));
            return new(false, "'" + observed.Line.Text + "' is no longer visible; nothing was tapped. Visible now: " + visible + ".", null);
        }
        ScreenPoint point = window.PointAt(current.CenterX, current.CenterY);
        UiOutcome tapped = screen.Tap(window, point);
        return new(tapped.Success, tapped.Message, new
        {
            x = point.X,
            y = point.Y,
            text = current.Text
        });
    }

    // The same text nearest to where it was observed, or null when it is gone or has moved too far to be the same one.
    private static RecognizedLine? FreshMatch(ObservedText observed, IReadOnlyList<RecognizedLine> lines)
    {
        RecognizedLine before = observed.Line;
        RecognizedLine? nearest = lines.Where(line => line.Text == before.Text)
            .OrderBy(line => Distance(line, before))
            .FirstOrDefault();

        return nearest != null && Distance(nearest, before) <= SameTextDistance ? nearest : null;
    }

    private static double Distance(RecognizedLine first, RecognizedLine second)
    {
        double horizontal = first.CenterX - second.CenterX;
        double vertical = first.CenterY - second.CenterY;

        return Math.Sqrt(horizontal * horizontal + vertical * vertical);
    }

    // A point is the explicit last resort for a control without text; it must lie in the mirroring window as it is now.
    // Throws InvalidOperationException when the mirroring window is not visible.
    private ClickOutcome TapPoint(int x, int y)
    {
        MirroredWindow window = RequireWindow();
        bool inside = x >= window.X && x < window.X + window.Width && y >= window.Y && y < window.Y + window.Height;
        if (!inside)
        {
            return new(false, "The point lies outside the mirroring window; nothing was tapped.", null);
        }

        UiOutcome tapped = screen.Tap(window, new ScreenPoint(x, y));
        return new(tapped.Success, tapped.Message, new
        {
            x,
            y
        });
    }

    // Typing replaces nothing: the field is tapped where its text is now, then the text is typed as key events.
    // Throws InvalidOperationException when the mirroring window is not visible or cannot be read.
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int autoWaitMs)
    {
        if (clear)
        {
            return new(false, "A mirrored phone cannot clear a field reliably; type into an empty field.");
        }

        ClickOutcome tapped = TapFresh(id);
        if (!tapped.Success)
        {
            return new(false, tapped.Message);
        }
        MirroredWindow window = RequireWindow();
        UiOutcome typed = screen.Type(window, text);
        return typed.Success && enter ? screen.Keys(window, ["enter"]) : typed;
    }

    public UiOutcome SetText(int id, string text, int autoWaitMs = 3000)
    {
        return TypeText(id, text, true, false, autoWaitMs);
    }

    // Throws InvalidOperationException when the mirroring window is not visible.
    public UiOutcome KeyChord(string[] keys)
    {
        return screen.Keys(RequireWindow(), keys);
    }

    public ScreenshotOutcome Screenshot()
    {
        MirroredWindow? window = screen.FindWindow();
        if (window == null)
        {
            return new(false, "The mirroring window is not visible.", null);
        }

        return new(true, "Mirroring window captured; nothing else on the screen.", screen.Capture(window));
    }

    public UiOutcome ClickElement(int id, int autoWaitMs = 3000)
    {
        return new(false, "A mirrored phone has no semantic invocation; use mouse_click on the recognized text.");
    }

    public UiOutcome SilentClick(int id)
    {
        return ClickElement(id);
    }

    public string GetChapters(int? pid, string? titleQuery)
    {
        return "error: A mirrored phone has no sections; use get_ui_tree.";
    }

    public string InspectSection(string chapterId, int maxDepth = 3)
    {
        return "error: A mirrored phone has no sections; use get_ui_tree.";
    }

    public GeometryOutcome SetWindowGeometry(int? pid, string? titleQuery, string? state, int? width, int? height, int? x, int? y)
    {
        return new(false, "The mirroring window keeps its own size.", null);
    }

    public UiOutcome LaunchApp(string path, IReadOnlyList<string> arguments)
    {
        return new(false, "Open apps on a mirrored phone by tapping their names.");
    }

    public UiOutcome CloseWindow(int? pid, string? title)
    {
        return new(false, "The mirroring window is not closed by the node.");
    }

    public UiOutcome MouseMove(int x, int y)
    {
        return new(false, "A mirrored phone has no pointer to move.");
    }

    public UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY)
    {
        return new(false, "Swipes on a mirrored phone are not implemented yet.");
    }

    public ClipboardOutcome GetClipboard()
    {
        return new(false, "The mirrored phone's clipboard is not read by the node.", null);
    }

    public UiOutcome SetClipboard(string text)
    {
        return new(false, "The mirrored phone's clipboard is not written by the node.");
    }

    public UiOutcome PasteClipboard(int? targetId = null)
    {
        return new(false, "The mirrored phone's clipboard is not used by the node.");
    }
}
