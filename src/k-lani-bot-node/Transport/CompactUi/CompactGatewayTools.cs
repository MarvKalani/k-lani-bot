using System.Reflection;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Compact routing converts the finite public tool family once at the MCP boundary.
public enum CompactUiTool
{
    Info,
    Observe,
    Act,
    Apply,
    Input,
    CartridgeInfo,
    CartridgeBrowse,
    CartridgeCall
}

// The compact MCP edge exposes its routing descriptors from an embedded contract, with native schemas loaded privately on demand.
public static class CompactGatewayTools
{
    // Some clients never show server instructions, so this recipe is a second channel beside the ui_info reply, which stays primary.
    public const string Instructions = "Author missing task procedures, then use them yourself through MCP. "
        + "Identify the application/build; on version drift, test the needed behavior before adapting or deriving its cartridge. "
        + "Explore with the available screenshot/UI controls, parameterize and verify the procedure, and reuse it with fresh inputs. "
        + "Aim for one business-operation call; keep deterministic steps out of model context. "
        + "Programming.learn captures supported ui_act demonstrations; other procedures need a loaded cartridge. "
        + "Call ui_info first; each window row lists the stored functions it can run. "
        + "Run a listed function with cartridge_call {function, arguments: {pid, parameters: {name: value}}}. "
        + "When the function verifies the requested business result, do not observe it again. "
        + "A window without an accessibility tree is operated with ui_input as a last resort; its coordinates never enter a function.";

    private static readonly JsonArray Tools = JsonNode.Parse(Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("KLaniBot.compact-ui-tools.json")!)!["tools"]!.AsArray();

    public static JsonArray Descriptors()
    {
        return Tools.DeepClone().AsArray();
    }

    public static bool TryDescriptor(string name, out JsonObject? descriptor)
    {
        descriptor = Tools.OfType<JsonObject>().FirstOrDefault(tool => tool["name"]!.ToString() == name);

        return descriptor != null;
    }

    public static bool TryTool(string name, out CompactUiTool tool)
    {
        CompactUiTool? parsed = name switch
        {
            "ui_info" => CompactUiTool.Info,
            "ui_observe" => CompactUiTool.Observe,
            "ui_act" => CompactUiTool.Act,
            "ui_apply" => CompactUiTool.Apply,
            "ui_input" => CompactUiTool.Input,
            "cartridge_info" => CompactUiTool.CartridgeInfo,
            "cartridge_browse" => CompactUiTool.CartridgeBrowse,
            "cartridge_call" => CompactUiTool.CartridgeCall,
            _ => null
        };
        tool = parsed ?? CompactUiTool.Info;

        return parsed != null;
    }
}
