using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KLaniBot.Common;

namespace KLaniBot.Actions;

// Use case: every tool in contracts/tools.json names the feature it needs, and every backend reports which features work
// right now. The dispatcher refuses a desktop tool whose feature the backend lacks, and get_machine_capabilities lists the
// backend's features by these words. ToolFeatureCases pins that every word in the contract is a value here.
[JsonConverter(typeof(ToolFeatureJsonConverter))]
public enum ToolFeature
{
    // Tools the node answers itself: capabilities, status, inventory, session end.
    [JsonStringEnumMemberName("meta")]
    Meta,

    [JsonStringEnumMemberName("macros")]
    Macros,

    // Files inside the agent workspace.
    [JsonStringEnumMemberName("filesystem")]
    Filesystem,

    [JsonStringEnumMemberName("terminal")]
    Terminal,

    [JsonStringEnumMemberName("process")]
    Process,

    // List the open windows.
    [JsonStringEnumMemberName("windows")]
    Windows,

    // Read and change a window's position and size, or close it.
    [JsonStringEnumMemberName("windowControl")]
    WindowControl,

    // Read the accessibility tree.
    [JsonStringEnumMemberName("semanticInspect")]
    SemanticInspect,

    [JsonStringEnumMemberName("applicationObjects")]
    ApplicationObjects,

    // Press a control through the accessibility interface.
    [JsonStringEnumMemberName("semanticInvoke")]
    SemanticInvoke,

    // Press a control without bringing its window to the front.
    [JsonStringEnumMemberName("backgroundInvoke")]
    BackgroundInvoke,

    // The shared mouse and keyboard.
    [JsonStringEnumMemberName("physicalInput")]
    PhysicalInput,

    // Set a field's text without typing it key by key.
    [JsonStringEnumMemberName("textInjection")]
    TextInjection,

    [JsonStringEnumMemberName("screenshot")]
    Screenshot,

    [JsonStringEnumMemberName("clipboard")]
    Clipboard,

    // Answered by the gateway, never by a node backend.
    [JsonStringEnumMemberName("fleet")]
    Fleet,

    // Answered by the gateway, never by a node backend.
    [JsonStringEnumMemberName("classifier")]
    Classifier,

    // Encrypted archives of the operator's backup source; answered inside the node.
    [JsonStringEnumMemberName("backup")]
    Backup,

    // A program's own MCP server that the operator listed in the policy; it runs beside the node, not on the screen.
    [JsonStringEnumMemberName("applicationMcp")]
    ApplicationMcp
}

// A number in place of a word would name whatever feature has that position today, so only words are read.
public sealed class ToolFeatureJsonConverter() : JsonStringEnumConverter<ToolFeature>(namingPolicy: null, allowIntegerValues: false);

// The dispatcher, the guard and the indicator ask here which feature a contract entry names and what that feature means for the desktop.
public static class ToolFeatures
{
    // Throws InvalidDataException when the tool names no known feature. The contract is embedded and a test reads every
    // tool's feature, so this only happens with a broken build.
    public static ToolFeature Of(JsonObject tool)
    {
        string? word = tool["feature"]?.ToString();

        if (!WireNames<ToolFeature>.TryParse(word, out ToolFeature feature))
        {
            throw new InvalidDataException("Tool " + tool["name"] + " names an unknown feature.");
        }
        return feature;
    }

    // Tools of the other features run inside the node. These drive the desktop through the backend and need a desktop
    // lease, a visible indicator and the matching backend feature; no backend reports fleet or classifier, so a node
    // refuses those tools and only the gateway answers them.
    public static bool UsesDesktop(ToolFeature feature)
    {
        return feature switch
        {
            ToolFeature.Meta or ToolFeature.Macros or ToolFeature.Filesystem or ToolFeature.Terminal or ToolFeature.Process
                or ToolFeature.Backup or ToolFeature.ApplicationMcp => false,
            ToolFeature.Windows or ToolFeature.WindowControl or ToolFeature.SemanticInspect or ToolFeature.ApplicationObjects
                or ToolFeature.SemanticInvoke
                or ToolFeature.BackgroundInvoke or ToolFeature.PhysicalInput or ToolFeature.TextInjection or ToolFeature.Screenshot
                or ToolFeature.Clipboard or ToolFeature.Fleet or ToolFeature.Classifier => true,
            _ => throw new UnreachableException()
        };
    }

    // The features the person at the machine must see as a border while they are used: everything that reads or moves
    // things on the screen.
    public static bool ShowsBorder(ToolFeature feature)
    {
        return feature switch
        {
            ToolFeature.Windows or ToolFeature.WindowControl or ToolFeature.SemanticInspect or ToolFeature.ApplicationObjects
                or ToolFeature.SemanticInvoke
                or ToolFeature.BackgroundInvoke or ToolFeature.PhysicalInput or ToolFeature.TextInjection or ToolFeature.Screenshot
                or ToolFeature.Clipboard => true,
            ToolFeature.Meta or ToolFeature.Macros or ToolFeature.Filesystem or ToolFeature.Terminal or ToolFeature.Process
                or ToolFeature.Fleet or ToolFeature.Classifier or ToolFeature.Backup or ToolFeature.ApplicationMcp => false,
            _ => throw new UnreachableException()
        };
    }
}
