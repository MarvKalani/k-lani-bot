namespace KLaniBot.Transport.CompactUi;

// Use case: ui_info names the programs whose own MCP server the node offers (Programmable App MCPs), so an agent finds the
// app_mcp functions without being told; the hint says the one call that lists a program's tools and the one that runs them.
public static class CompactApplicationMcpNames
{
    public const string Field = "app_mcps";

    public const string Hint = "app_mcps names programs with their own MCP server: cartridge_call {function: \"app_mcp_read\", "
        + "arguments: {program}} lists its tools (read tools run there too, schema loads one tool's schema); "
        + "\"app_mcp_call\" with {program, tool, arguments} runs any of its tools as a change. After a sequence worked, "
        + "\"app_mcp_save\" with {program, name, description, parameters: [{name, type}], steps: [{tool, arguments}], checks: [{kind: "
        + "file_written|gif_frames, path}], test: {name: value}} reruns it with the test values and stores it as one function; "
        + "\"{{name}}\" fills a parameter, {\"$step\": n, \"pointer\": \"/items/0\"} a value of step n's answer.";
}
