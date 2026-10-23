namespace KLaniBot.Transport;

// The first text an MCP client passes to its model: what this server is and how to start, ahead of any authoring advice.
// Small models act on what they read first; without it an agent mistook nodes, cartridges and application MCPs for one
// another and opened a new connection per call, which loses the desktop lease.
internal static class GatewayOrientation
{
    public const string Identity = "k-lani-bot is remote computer use: you see and operate the desktop of other registered "
        + "computers (nodes) through their UI elements, like Computer Use with fewer screenshots. "
        + "Saved, reusable action sequences for one application are cartridges (RPA). ";

    public const string Start = "Start with list_nodes, then get_machine_capabilities for the node: it says what you may do "
        + "there. Operate with list_windows, then find_elements or get_ui_tree, which return element ids and a snapshot_id, "
        + "then click_element or type_text with them, then verify_element or take_screenshot. "
        + "There is no shell tool: to run a command, open a terminal on the node (hotkey win+r, type powershell, Enter), "
        + "type the command with enter: true and read the result with take_screenshot. ";

    public const string Session = "Keep one MCP connection for a whole task: a node's desktop belongs to one connection for "
        + "five minutes, renewed by every call, and other connections are refused until then; call end_session when done. "
        + "The person at the node sees a red border and your client name; set K_LANI_BOT_TASK in the gateway's environment "
        + "to show your task there too. ";
}
