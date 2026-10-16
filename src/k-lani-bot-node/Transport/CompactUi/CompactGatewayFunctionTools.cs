using System.Reflection;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Selected compact function schemas are embedded data and loaded individually; unimplemented branches are not advertised to the model.
public static class CompactGatewayFunctionTools
{
    public static readonly JsonArray All = JsonNode.Parse(Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("KLaniBot.compact-ui-functions.json")!)!["functions"]!.AsArray();

    public static JsonObject? Find(string name)
    {
        return All.OfType<JsonObject>().FirstOrDefault(function => function["name"]!.ToString() == name);
    }
}
