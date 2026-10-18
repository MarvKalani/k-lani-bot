using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Platforms.Mirroring;

namespace KLaniBot.Platforms.MacOS;

// Use case: a macOS node operates a real iPhone through the iPhone Mirroring app. The bundled Swift helper finds the app's window
// through the window server, captures only that window and recognizes its text with Apple Vision; taps and typing go through the
// node's own Quartz events. The helper answers one JSON object per call, so no image leaves the node unless asked for.
public sealed class MacMirroredScreen(MacUiService desktop, string helperPath, string bundleIdentifier) : IMirroredScreen
{
    // The bundle identifier of iPhone Mirroring as the window server reports it for the app's process.
    public const string IphoneMirroringBundle = "com.apple.ScreenContinuity";
    public const string HelperName = "k-lani-bot-mirror";
    private const int HelperTimeoutMilliseconds = 20000;
    private const int ActivationSettleMilliseconds = 250;

    // The helper next to the node executable in the app bundle.
    public static string BundledHelperPath()
    {
        string directory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        return Path.Combine(directory, HelperName);
    }

    // Throws InvalidOperationException when the helper fails or answers something else than a window description.
    public MirroredWindow? FindWindow()
    {
        JsonObject answer = Helper("window", "--bundle", bundleIdentifier);

        if (answer["found"]?.GetValue<bool>() != true)
        {
            return null;
        }
        return new MirroredWindow((long)Number(answer, "window_id"), (int)Number(answer, "pid"), Number(answer, "x"), Number(answer, "y"),
            Number(answer, "width"), Number(answer, "height"));
    }

    // Throws InvalidOperationException when the helper cannot capture the window or recognize its text.
    public IReadOnlyList<RecognizedLine> Read(MirroredWindow window)
    {
        JsonObject answer = Helper("read", "--window", window.WindowId.ToString());

        var lines = new List<RecognizedLine>();
        foreach (JsonObject line in (answer["lines"] as JsonArray ?? []).OfType<JsonObject>())
        {
            lines.Add(new RecognizedLine(line["text"]?.ToString() ?? "", Number(line, "confidence"), Number(line, "x"),
                Number(line, "y"), Number(line, "width"), Number(line, "height")));
        }
        return lines;
    }

    // Throws InvalidOperationException when the helper cannot capture the window.
    public byte[] Capture(MirroredWindow window)
    {
        string file = Path.Combine(Path.GetTempPath(), "k-lani-bot-mirror-" + Guid.CreateVersion7() + ".png");
        try
        {
            Helper("capture", "--window", window.WindowId.ToString(), "--out", file);
            return File.ReadAllBytes(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public UiOutcome Tap(MirroredWindow window, ScreenPoint point)
    {
        Activate(window);

        ClickOutcome clicked = desktop.MouseClick(0, "left", (int)Math.Round(point.X), (int)Math.Round(point.Y));
        return new(clicked.Success, clicked.Success ? "Tap dispatched to the mirrored phone." : clicked.Message);
    }

    public UiOutcome Type(MirroredWindow window, string text)
    {
        Activate(window);

        return desktop.TypeIntoFrontmost(window.ProcessId, text);
    }

    public UiOutcome Keys(MirroredWindow window, string[] keys)
    {
        Activate(window);

        return desktop.KeyChord(keys);
    }

    // The mirroring app takes input only while it is frontmost.
    // Throws InvalidOperationException when the helper cannot bring it to the front.
    private void Activate(MirroredWindow window)
    {
        Helper("activate", "--pid", window.ProcessId.ToString());

        Thread.Sleep(ActivationSettleMilliseconds);
    }

    // Throws InvalidOperationException when the helper fails, misses its deadline or answers no JSON object.
    private JsonObject Helper(params string[] arguments)
    {
        ProcessResult result = ProcessRunner.Run(helperPath, arguments, HelperTimeoutMilliseconds);
        if (!result.Success)
        {
            throw new InvalidOperationException("The mirroring helper failed: " + result.Error.Trim());
        }

        try
        {
            return JsonNode.Parse(result.Output) as JsonObject
                ?? throw new InvalidOperationException("The mirroring helper answered no JSON object.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("The mirroring helper answered no JSON object.");
        }
    }

    private static double Number(JsonObject value, string key)
    {
        return value[key] is JsonValue number && number.TryGetValue(out double result) ? result : 0;
    }
}
