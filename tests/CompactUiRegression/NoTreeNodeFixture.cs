using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Use case: the ui_input cases need a node whose one window publishes no accessibility tree, as an egui program's window does,
// while geometry, screenshot and raw input answer like the real node and every mutation is kept for inspection.
internal sealed class NoTreeNodeFixture : ICompactNodeClient
{
    public const string Image = "iVBORw0KGgo=";
    private const int Width = 1280;
    private const int Height = 690;

    public List<NoTreeMutation> Mutations { get; } = [];

    // The window's left edge; a case moves the window by changing it.
    public int Left
    {
        get; set;
    }

    // A tree with a button under the window row, so the window belongs to ui_observe and ui_act.
    public bool WithTree
    {
        get; set;
    }

    // The node refuses hotkey as the guarded profile does for raw keys.
    public bool RefuseHotkey
    {
        get; set;
    }

    public Task<JsonObject> CallAsync(Tool tool, JsonObject arguments, CancellationToken cancellation)
    {
        if (ToolContract.Get(tool)["mutates"]?.GetValue<bool>() == true)
        {
            Mutations.Add(new NoTreeMutation(tool, arguments.DeepClone().AsObject()));
            return Task.FromResult(Mutation(tool));
        }

        return Task.FromResult(Read(tool));
    }

    private JsonObject Read(Tool tool)
    {
        return tool switch
        {
            Tool.GetMachineCapabilities => new JsonObject
            {
                ["success"] = true,
                ["capabilities"] = new JsonObject
                {
                    ["nodeId"] = "fixture-node",
                    ["version"] = "synthetic",
                    ["platform"] = "linux-x11-atspi"
                }
            },
            Tool.ListWindows => new JsonObject { ["success"] = true, ["windows"] = new JsonArray(Window()) },
            Tool.GetUiTree => Tree(),
            Tool.GetWindowGeometry => new JsonObject
            {
                ["success"] = true,
                ["bounds"] = new JsonObject { ["x"] = Left, ["y"] = 0, ["width"] = Width, ["height"] = Height }
            },
            Tool.TakeScreenshot => new JsonObject { ["success"] = true, ["image"] = Image, ["mime_type"] = "image/png" },
            _ => new JsonObject { ["success"] = false, ["error"] = "Unexpected read in the no-tree fixture." }
        };
    }

    private JsonObject Mutation(Tool tool)
    {
        if (tool == Tool.Hotkey && RefuseHotkey)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["outcome"] = "not_dispatched",
                ["code"] = "denied",
                ["error"] = "Node guard blocks raw keys, coordinates, shell and process authority in guarded mode."
            };
        }

        return new JsonObject { ["success"] = true, ["outcome"] = "dispatched" };
    }

    private JsonObject Tree()
    {
        if (WithTree)
        {
            return new JsonObject
            {
                ["success"] = true,
                ["tree"] = "[1] Window \"Editor\"\n  [2] Button \"Import\"",
                ["truncated"] = true,
                ["snapshot_id"] = "snapshot"
            };
        }

        return new JsonObject
        {
            ["success"] = false,
            ["code"] = ActionErrorCode.NoAccessibilityTree.ToWireName(),
            ["error"] = "error: The accessibility bus shows no tree for this window."
        };
    }

    private static JsonObject Window()
    {
        return new JsonObject
        {
            ["ProcessId"] = 10,
            ["ProcessName"] = "filmcraft",
            ["AppVersion"] = "0.8.0",
            ["Title"] = "Editor",
            ["WindowHandle"] = 100L,
            ["Kind"] = "main",
            ["ProcessStartedAtUtc"] = "2026-10-17T00:00:00Z"
        };
    }
}

// Use case: one mutation the no-tree fixture received, with the arguments the gateway sent.
internal sealed record NoTreeMutation(Tool Tool, JsonObject Arguments);
