using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Use case: an agent must operate a window whose program publishes no accessibility tree, such as an egui video editor. ui_input is
// the explicit last resort: it proves the tree is absent, binds click coordinates to the latest screenshot of the same window
// instance at the same rectangle, sends input through the node (policy, consent, guard, backup and journal apply there) and
// returns a fresh screenshot so the agent checks the result. Nothing of it becomes a stored function.
public sealed class CompactGatewayInput(ICompactNodeClient client, CompactGatewayObservation observation, CompactGatewayState state)
{
    // Long enough for a menu or list to redraw after the input before the screenshot that shows the result.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(400);

    // A tree read that returns more than this many rows already holds an element besides the window row.
    private const int TreeProbeRows = 2;

    private readonly CompactNodeAccess _node = new(client, state.Node!);
    private int _completed;

    // Throws ArgumentException for malformed steps; node refusals and failed checks become failed replies without any replay.
    public async Task<JsonObject> RunAsync(JsonObject arguments, CancellationToken cancellation)
    {
        CompactInputRequest request = CompactInputCodec.Read(arguments);
        var selection = state.SelectWindow(arguments);
        state.Demonstration = null;

        try
        {
            CompactNativeWindow window = await CurrentWindowAsync(selection, cancellation);
            CompactSemanticAbsence absence = await RequireNoTreeAsync(window, cancellation);
            if (request.Steps.Count > 0)
            {
                RequireClickTarget(request, window, await BoundsAsync(window, cancellation));
                await SendStepsAsync(request, selection, window, absence, cancellation);
                await Task.Delay(SettleTime, cancellation);
                await RequireSameInstanceAsync(selection, window, cancellation);
            }
            return await ReplyAsync(window, absence, cancellation);
        }
        catch (CompactNodeException exception)
        {
            state.InputShot = null;
            return Failure(exception.Message);
        }
    }

    // Throws CompactNodeException unless the named handle is a current window of an instance that ui_info listed.
    private async Task<CompactNativeWindow> CurrentWindowAsync(CompactWindowSelection selection, CancellationToken cancellation)
    {
        var windows = await observation.RequireInstanceAsync(selection.ProcessId, cancellation);
        if (!CompactWindowChoice.TryChoose(windows, selection, out var window))
        {
            throw new CompactNodeException(CompactWindowChoice.UnknownHandleRefusal);
        }
        bool listed = state.ObservedWindows.Any(row => row.Handle == window.Handle && row.ProcessId == window.ProcessId
            && row.Started != null && row.Started == window.Started);
        if (!listed)
        {
            throw new CompactNodeException(CompactInputNames.ReplacedRefusal);
        }

        return window;
    }

    // Throws CompactNodeException when the chosen window was replaced or closed while the steps ran.
    private async Task RequireSameInstanceAsync(CompactWindowSelection selection, CompactNativeWindow window,
        CancellationToken cancellation)
    {
        var current = await CurrentWindowAsync(new CompactWindowSelection(selection.ProcessId, window.Handle), cancellation);
        if (current.Started != window.Started)
        {
            throw new CompactNodeException(CompactInputNames.ReplacedRefusal);
        }
    }

    // Throws CompactNodeException for a window with a tree, or when the node's read neither shows a tree nor proves its absence.
    private async Task<CompactSemanticAbsence> RequireNoTreeAsync(CompactNativeWindow window, CancellationToken cancellation)
    {
        var arguments = new JsonObject
        {
            ["pid"] = window.ProcessId,
            ["window"] = window.Handle,
            ["max_depth"] = 1,
            ["max_nodes"] = TreeProbeRows,
            ["row_format"] = CompactNodeAccess.CommonRowFormat
        };
        JsonObject response = await client.CallAsync(Tool.GetUiTree, arguments, cancellation);
        var result = CompactNodeResponse.Read(response);

        if (!result.Success)
        {
            return result.Code == ActionErrorCode.NoAccessibilityTree ? CompactSemanticAbsence.NoTree
                : throw new CompactNodeException(CompactInputNames.TreeUnknownRefusal + (response["error"]?.ToString() ?? "unknown"));
        }
        bool onlyWindowRow = response["truncated"]?.GetValue<bool>() != true && TreeRows(response) <= 1;

        return onlyWindowRow ? CompactSemanticAbsence.EmptyTree : throw new CompactNodeException(CompactInputNames.TreePresentRefusal);
    }

    private static int TreeRows(JsonObject response)
    {
        string tree = response["tree"]?.ToString() ?? "";

        return tree.Split('\n').Count(line => line.Trim().Length > 0);
    }

    // Throws CompactNodeException when the node cannot report the window's rectangle in screenshot pixels.
    private async Task<WindowScreenBounds> BoundsAsync(CompactNativeWindow window, CancellationToken cancellation)
    {
        var arguments = new JsonObject { ["pid"] = window.ProcessId, ["window"] = window.Handle };
        JsonObject response = CompactNodeResponse.Require(await client.CallAsync(Tool.GetWindowGeometry, arguments, cancellation));

        return ReadBounds(response["bounds"]);
    }

    // Throws CompactNodeException for a reply without a complete, positive rectangle.
    private static WindowScreenBounds ReadBounds(JsonNode? bounds)
    {
        if (bounds is not JsonObject rectangle || !TryInteger(rectangle, "x", out int x) || !TryInteger(rectangle, "y", out int y)
            || !TryInteger(rectangle, "width", out int width) || !TryInteger(rectangle, "height", out int height)
            || width <= 0 || height <= 0)
        {
            throw new CompactNodeException("The node reported no complete window rectangle.");
        }

        return new WindowScreenBounds(x, y, width, height);
    }

    private static bool TryInteger(JsonObject source, string name, out int value)
    {
        value = 0;

        return source[name] is JsonValue number && number.TryGetValue(out value);
    }

    // Throws CompactNodeException when a first-step click names no current shot of this window instance, the window moved since,
    // or the point lies outside it; nothing has been sent at that moment.
    private void RequireClickTarget(CompactInputRequest request, CompactNativeWindow window, WindowScreenBounds bounds)
    {
        CompactInputStep first = request.Steps[0];
        if (first.Action != CompactInputAction.Click)
        {
            return;
        }
        CompactInputShot? shot = state.InputShot;
        if (shot == null || shot.Id != request.Shot || shot.Handle != window.Handle || shot.Started != window.Started)
        {
            throw new CompactNodeException(CompactInputNames.ShotRequiredRefusal);
        }
        if (shot.Bounds != bounds)
        {
            throw new CompactNodeException(CompactInputNames.MovedRefusal);
        }

        bool inside = first.X >= bounds.X && first.X < bounds.X + bounds.Width && first.Y >= bounds.Y && first.Y < bounds.Y + bounds.Height;
        if (!inside)
        {
            throw new CompactNodeException(CompactInputNames.OutsideRefusal);
        }
    }

    // Throws CompactNodeException for a refused or unconfirmed input; later steps are not sent.
    private async Task SendStepsAsync(CompactInputRequest request, CompactWindowSelection selection, CompactNativeWindow window,
        CompactSemanticAbsence absence, CancellationToken cancellation)
    {
        string journaled = CompactInputNames.Word(absence) + ": " + request.Reason;
        var focus = new JsonObject { ["pid"] = window.ProcessId, ["window"] = window.Handle };
        RequireSent(await _node.MutateAsync(Tool.FocusWindow, focus, null, cancellation));
        foreach (CompactInputStep step in request.Steps)
        {
            await RequireSameInstanceAsync(selection, window, cancellation);
            await SendStepAsync(step, journaled, cancellation);
            _completed++;
        }
    }

    // Throws CompactNodeException for a refused or unconfirmed input.
    private async Task SendStepAsync(CompactInputStep step, string journaled, CancellationToken cancellation)
    {
        switch (step.Action)
        {
            case CompactInputAction.Click:
                await SendAsync(Tool.MouseClick, new JsonObject
                {
                    ["x"] = step.X,
                    ["y"] = step.Y,
                    ["button"] = CompactInputNames.Word(step.Button)
                }, journaled, cancellation);
                break;
            case CompactInputAction.Key:
                await SendAsync(Tool.Hotkey, Keys(step.Keys), journaled, cancellation);
                break;
            case CompactInputAction.Type:
                RequireSent(await _node.MutateAsync(Tool.SetClipboard, new JsonObject { ["text"] = step.Text }, null, cancellation));
                await SendAsync(Tool.Hotkey, Keys(["ctrl", "v"]), journaled, cancellation);
                break;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static JsonObject Keys(IReadOnlyList<string> keys)
    {
        return new JsonObject { ["keys"] = new JsonArray(keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) };
    }

    // Throws CompactNodeException for a refused or unconfirmed input. The reason rides along, so the node journals it before dispatch.
    private async Task SendAsync(Tool tool, JsonObject arguments, string journaled, CancellationToken cancellation)
    {
        arguments["last_resort_reason"] = journaled;

        RequireSent(await _node.MutateAsync(tool, arguments, null, cancellation));
    }

    // Throws CompactNodeException for a refused, malformed or unconfirmed native mutation.
    private static void RequireSent(JsonObject response)
    {
        CompactNodeResponse.Require(response);
        if (!CompactNodeResponse.Read(response).Outcome.ReportsSuccess())
        {
            throw new CompactNodeException("Native input outcome remains unconfirmed.");
        }
    }

    // Throws CompactNodeException when the rectangle or the screenshot cannot be read; the shot is issued only with both.
    private async Task<JsonObject> ReplyAsync(CompactNativeWindow window, CompactSemanticAbsence absence, CancellationToken cancellation)
    {
        WindowScreenBounds bounds = await BoundsAsync(window, cancellation);
        JsonObject image = CompactNodeResponse.Require(await client.CallAsync(Tool.TakeScreenshot, new JsonObject(), cancellation));
        var shot = new CompactInputShot(Guid.NewGuid().ToString("N"), window.Handle, window.Started, bounds);
        state.InputShot = shot;

        return new JsonObject
        {
            ["success"] = true,
            ["pid"] = window.ProcessId,
            ["window"] = window.Handle,
            ["kind"] = CompactWindowKindWords.Text(window.Kind),
            ["semantic_route"] = CompactInputNames.Word(absence),
            ["completed_steps"] = _completed,
            ["bounds"] = new JsonObject { ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height },
            ["shot"] = shot.Id,
            ["next"] = CompactInputNames.Next,
            ["image"] = image["image"]?.DeepClone(),
            ["mime_type"] = image["mime_type"]?.DeepClone()
        };
    }

    private JsonObject Failure(string error)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["error"] = error,
            ["completed_steps"] = _completed,
            ["failed_step_may_have_changed_ui"] = _node.PossibleDispatches > 0,
            ["retry"] = CompactInputNames.Retry
        };
    }
}
