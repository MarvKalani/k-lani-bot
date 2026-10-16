using System.Reflection;
using System.Text.Json.Nodes;
using KLaniBot.Transport;

namespace KLaniBot.Actions;

// Use case: the loopback diagnostics server shows a small page where an operator can try tools by hand.
// What the page must know about each tool (does it need a request_id of its own?) comes from the tool
// contract at the moment the page is served. A copy typed into the page drifted from the contract once:
// it left out take_screenshot, collect_inventory, get_macro and get_macro_trace, which the schema then
// rejected, and it stripped the request_id that get_action_status needs.
public static class InspectorPage
{
    private const string ToolTablePlaceholder = "__TOOL_TABLE__";
    private const string SessionHeaderPlaceholder = "__SESSION_HEADER__";

    // Throws InvalidOperationException when the page resource is missing from this build.
    public static string Build()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KLaniBot.inspector.html")
            ?? throw new InvalidOperationException("The inspector page is not part of this build.");
        using var reader = new StreamReader(stream);
        string template = reader.ReadToEnd();
        return template
            .Replace(ToolTablePlaceholder, DescribeTools().ToJsonString(), StringComparison.Ordinal)
            .Replace(SessionHeaderPlaceholder, ProtocolIdentity.SessionHeader, StringComparison.Ordinal);
    }

    // A request_id is generated only for a mutating tool that accepts one. A reading tool that takes a
    // request_id as its subject (get_action_status looks up another operation) must get the caller's own value.
    public static JsonObject DescribeTools()
    {
        var table = new JsonObject();
        foreach (var tool in ToolContract.Tools.OfType<JsonObject>())
        {
            bool mutates = tool["mutates"]!.GetValue<bool>();
            bool acceptsRequestId = tool["inputSchema"]!["properties"]?["request_id"] != null;
            table[tool["name"]!.ToString()] = new JsonObject
            {
                ["add_request_id"] = mutates && acceptsRequestId
            };
        }
        return table;
    }
}
