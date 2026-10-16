using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace KLaniBot.Visibility;

public sealed partial class MacVisibilityHost
{
    public static void TestAssignmentOffscreen(string directory)
    {
        NativeLibrary.Load(AppKit);
        IntPtr pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
        IntPtr application = Send(Class("NSApplication"), "sharedApplication");
        Long(application, "setActivationPolicy:", 2);
        var details = SessionPresentation.Create(
            "Native test client",
            "Synthetisches Dokument prüfen",
            "master-fixture",
            "node-fixture",
            "observe, ui");
        var state = new VisibilityState(VisibilityStatus.Active, 1, true, null, false, true, details);
        AssignmentWindow assignment = CreateAssignmentWindow(new Rect(0, 0, 1000, 72), DescribeAssignment(state));
        try
        {
            if (ReadLong(assignment.Window, Sel("isVisible")) != 0 || ReadLong(assignment.Window, Sel("ignoresMouseEvents")) == 0
                || ReadLong(assignment.Window, Sel("level")) < 1000)
            {
                throw new Exception("Offscreen assignment window violated visibility or input contract.");
            }
            RecordStages(assignment, state, details, directory);
            Console.WriteLine(
                "Mac native assignment checks passed: production AppKit renderer, native input/level properties and updated "
                + "identity/task/status text. Owned offscreen-view PNGs recorded; no compositor/Spaces acceptance claimed.");
        }
        finally
        {
            Send(assignment.Window, "close");
            Send(assignment.Window, "release");
            Send(pool, "drain");
        }
    }

    // Each stage sets the banner text the node would show and records it as rendered by AppKit.
    private static void RecordStages(AssignmentWindow assignment, VisibilityState state, SessionPresentation details, string directory)
    {
        var stages = new Dictionary<string, VisibilityState>
        {
            ["active"] = state,
            ["granted"] = state with { Status = VisibilityStatus.Connected },
            ["changed"] = state with
            {
                Presentation = details with
                {
                    Client = "Another test client",
                    Purpose = "Zweiter synthetischer Auftrag"
                }
            }
        };
        foreach ((string stage, VisibilityState current) in stages)
        {
            string expected = DescribeAssignment(current);
            Pointer(assignment.Label, "setStringValue:", Str(expected));
            string actual = Marshal.PtrToStringUTF8(Send(Send(assignment.Label, "stringValue"), "UTF8String"))!;
            if (actual != expected)
            {
                throw new Exception("AppKit retained stale or incomplete assignment text.");
            }
            CacheOwnView(Send(assignment.Window, "contentView"), Path.Combine(directory, stage + ".png"));
            File.WriteAllText(Path.Combine(directory, stage + ".json"), new JsonObject
            {
                ["native_appkit"] = true,
                ["offscreen"] = true,
                ["physical_desktop_used"] = false,
                ["click_through"] = true,
                ["level"] = (long)ReadLong(assignment.Window, Sel("level")),
                ["text"] = actual
            }.ToJsonString());
        }
    }
    public void WriteEvidence(string directory, string name) => Invoke(() =>
    {
        var evidence = new JsonObject
        {
            ["status_item_visible"] = ReadLong(_status, Sel("isVisible")) != 0,
            ["border_count"] = _borders.Count,
            ["diagnostic_simulation"] = true,
            ["windows"] = new JsonArray()
        };
        var rows = evidence["windows"]!.AsArray();
        int i = 0;
        CacheOwnView(_button, Path.Combine(directory, name + "-menubar.png"));
        if (_question != IntPtr.Zero)
            CacheOwnView(Send(_question, "contentView"), Path.Combine(directory, name + "-question.png"));
        foreach (var window in _borders)
        {
            var rect = Frame(window, "frame");
            bool clickThrough = ReadLong(window, Sel("ignoresMouseEvents")) != 0;
            long level = (long)ReadLong(window, Sel("level"));
            if (!clickThrough || level < 1000 || ReadLong(window, Sel("isVisible")) == 0)
                throw new Exception("Native border contract failed");
            rows.Add(
                new JsonObject
                {
                    ["click_through"] = clickThrough,
                    ["level"] = level,
                    ["x"] = rect.X,
                    ["y"] = rect.Y,
                    ["width"] = rect.W,
                    ["height"] = rect.H
                });
            CacheOwnView(Send(window, "contentView"), Path.Combine(directory, name + "-edge-" + (i++) + ".png"));
        }
        if (name == "active" && (_borders.Count == 0 || _borders.Count % 4 != 0))
            throw new Exception("Four native border windows per screen expected");
        File.WriteAllText(Path.Combine(directory, name + ".json"), evidence.ToJsonString());
    });
    public void InvokeMenuCommand(int tag) => Invoke(() =>
    {
        var menu = Send(_status, "menu");
        long count = (long)ReadLong(menu, Sel("numberOfItems"));
        for (nint index = 0; index < count; index++)
        {
            var item = SendLong(menu, Sel("itemAtIndex:"), index);
            if (ReadLong(item, Sel("tag")) == tag)
            {
                Long(menu, "performActionForItemAtIndex:", index);
                return;
            }
        }
        throw new Exception("Local menu command unavailable");
    });
    private static void CacheOwnView(IntPtr view, string file)
    {
        Rect bounds = Frame(view, "bounds");
        if (bounds.W <= 0 || bounds.H <= 0)
            throw new Exception("Own view has no drawable bounds");
        var bitmap = SendRect(view, Sel("bitmapImageRepForCachingDisplayInRect:"), bounds);
        CacheDisplay(view, Sel("cacheDisplayInRect:toBitmapImageRep:"), bounds, bitmap);
        var data = Send2(bitmap, Sel("representationUsingType:properties:"), new IntPtr(4), Send(Class("NSDictionary"), "dictionary"));
        int length = (int)ReadLong(data, Sel("length"));
        if (length <= 0 || length > 8 * 1024 * 1024)
            throw new Exception("Invalid owned view render");
        byte[] png = new byte[length];
        Marshal.Copy(Send(data, "bytes"), png, 0, length);
        File.WriteAllBytes(file, png);
    }
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern void CacheDisplay(IntPtr view, IntPtr selector, Rect bounds, IntPtr bitmap);
}
