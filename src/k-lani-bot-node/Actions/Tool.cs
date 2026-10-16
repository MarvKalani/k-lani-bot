using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KLaniBot.Common;

namespace KLaniBot.Actions;

// Use case: an AI client names a tool by its word in contracts/tools.json. The word is read once into this value, and
// every decision inside the node (handler, guard, consent, backend support) compares values, never words. ToolCases pins
// that the enum and the contract name the same tools.
[JsonConverter(typeof(ToolJsonConverter))]
public enum Tool
{
    [JsonStringEnumMemberName("get_machine_capabilities")]
    GetMachineCapabilities,

    [JsonStringEnumMemberName("list_available_mcps")]
    ListAvailableMcps,

    [JsonStringEnumMemberName("list_windows")]
    ListWindows,

    [JsonStringEnumMemberName("get_ui_tree")]
    GetUiTree,

    [JsonStringEnumMemberName("inspect_application")]
    InspectApplication,

    [JsonStringEnumMemberName("operate_application")]
    OperateApplication,

    [JsonStringEnumMemberName("get_chapters")]
    GetChapters,

    [JsonStringEnumMemberName("inspect_section")]
    InspectSection,

    [JsonStringEnumMemberName("find_elements")]
    FindElements,

    [JsonStringEnumMemberName("get_ui_delta")]
    GetUiDelta,

    [JsonStringEnumMemberName("focus_window")]
    FocusWindow,

    [JsonStringEnumMemberName("close_window")]
    CloseWindow,

    [JsonStringEnumMemberName("get_window_geometry")]
    GetWindowGeometry,

    [JsonStringEnumMemberName("set_window_geometry")]
    SetWindowGeometry,

    [JsonStringEnumMemberName("click_element")]
    ClickElement,

    [JsonStringEnumMemberName("silent_click")]
    SilentClick,

    [JsonStringEnumMemberName("type_text")]
    TypeText,

    [JsonStringEnumMemberName("take_screenshot")]
    TakeScreenshot,

    [JsonStringEnumMemberName("mouse_move")]
    MouseMove,

    [JsonStringEnumMemberName("mouse_click")]
    MouseClick,

    [JsonStringEnumMemberName("drag_and_drop")]
    DragAndDrop,

    [JsonStringEnumMemberName("hotkey")]
    Hotkey,

    [JsonStringEnumMemberName("launch_process")]
    LaunchProcess,

    [JsonStringEnumMemberName("wait_element")]
    WaitElement,

    [JsonStringEnumMemberName("verify_element")]
    VerifyElement,

    [JsonStringEnumMemberName("get_clipboard")]
    GetClipboard,

    [JsonStringEnumMemberName("set_clipboard")]
    SetClipboard,

    [JsonStringEnumMemberName("paste_clipboard")]
    PasteClipboard,

    [JsonStringEnumMemberName("list_macros")]
    ListMacros,

    [JsonStringEnumMemberName("save_macro")]
    SaveMacro,

    [JsonStringEnumMemberName("run_macro")]
    RunMacro,

    [JsonStringEnumMemberName("fs_list_directory")]
    FsListDirectory,

    [JsonStringEnumMemberName("fs_read_file")]
    FsReadFile,

    [JsonStringEnumMemberName("fs_write_file")]
    FsWriteFile,

    [JsonStringEnumMemberName("fs_search_files")]
    FsSearchFiles,

    [JsonStringEnumMemberName("fs_get_metadata")]
    FsGetMetadata,

    [JsonStringEnumMemberName("run_terminal_command")]
    RunTerminalCommand,

    [JsonStringEnumMemberName("get_action_status")]
    GetActionStatus,

    [JsonStringEnumMemberName("end_session")]
    EndSession,

    [JsonStringEnumMemberName("list_nodes")]
    ListNodes,

    [JsonStringEnumMemberName("evaluate_trace")]
    EvaluateTrace,

    [JsonStringEnumMemberName("get_macro")]
    GetMacro,

    [JsonStringEnumMemberName("get_macro_trace")]
    GetMacroTrace,

    [JsonStringEnumMemberName("collect_inventory")]
    CollectInventory,

    [JsonStringEnumMemberName("backup_plan")]
    BackupPlan,

    [JsonStringEnumMemberName("backup_verify")]
    BackupVerify,

    [JsonStringEnumMemberName("backup_create")]
    BackupCreate,

    [JsonStringEnumMemberName("backup_restore")]
    BackupRestore,

    [JsonStringEnumMemberName("app_mcp_read")]
    ApplicationMcpRead,

    [JsonStringEnumMemberName("app_mcp_call")]
    ApplicationMcpCall
}

// A number in place of a word would name whichever tool has that position today, so only words are read.
public sealed class ToolJsonConverter() : JsonStringEnumConverter<Tool>(namingPolicy: null, allowIntegerValues: false);

// The contract, the gateway and the journal name tools by their word; this converts between the word and the Tool value.
public static class ToolNames
{
    public static string Of(Tool tool)
    {
        return WireNames<Tool>.Of(tool);
    }

    public static bool TryParse(string? name, out Tool tool)
    {
        return WireNames<Tool>.TryParse(name, out tool);
    }

    // An entry of contracts/tools.json; ToolCases pins that every entry names a tool.
    public static Tool OfContractEntry(JsonObject entry)
    {
        return Parse(entry["name"]!.ToString());
    }

    // Throws ArgumentException for a word that names no tool, with the message the contract lookup gives.
    public static Tool Parse(string name)
    {
        if (!WireNames<Tool>.TryParse(name, out Tool tool))
        {
            throw new ArgumentException($"Unknown tool: {name}");
        }
        return tool;
    }
}
