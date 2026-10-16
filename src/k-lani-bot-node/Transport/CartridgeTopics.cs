using System.Text.Json.Nodes;

namespace KLaniBot.Transport;

// The model enters a program through a short category index and only loads the branch needed by its current task.
public enum CartridgeCategory
{
    Administration,
    Use,
    Info,
    Programming
}

// Native discovery can probe another build; position- or label-dependent recipes retain their exact tested build.
public enum CartridgeCompatibility
{
    ExactBuild,
    NativeProbe,
    ProgramLaunch
}

// Category names are typed at the manifest boundary; deeper segments are inventory keys, never filesystem paths.
public sealed record CartridgeTopic(CartridgeCategory Category, string[] Segments)
{
    public string Path => string.Join("/", new[] { Category.ToString() }.Concat(Segments));

    // Throws ArgumentException for an unknown category or an empty/escaping branch.
    public static CartridgeTopic Parse(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length is < 1 or > 6 || parts.Any(part => part.Length == 0 || part is "." or "..")
            || !Enum.TryParse(parts[0], out CartridgeCategory category) || !Enum.IsDefined(category))
        {
            throw new ArgumentException("Unknown cartridge category or invalid topic path.");
        }

        return new(category, parts.Skip(1).ToArray());
    }
}

// The three permanent gateway tools describe routing only; application schemas are returned on demand.
public static class CartridgeGatewayTools
{
    private const long MinimumProcessId = 1;
    public const string InfoName = "cartridge_info";
    public const string BrowseName = "cartridge_browse";
    public const string CallName = "cartridge_call";

    public static JsonArray Descriptors(bool requiresNode)
    {
        return new JsonArray(
            Descriptor(InfoName, "Identify the selected application before task tools; use pid when several processes match.",
                true, requiresNode,
                new JsonObject { ["pid"] = new JsonObject { ["type"] = "integer", ["minimum"] = MinimumProcessId } }),
            Descriptor(BrowseName, "Start with path Catalog, select its key through cartridge_call, then Info. "
                + "Browse only the task topic and individual function schemas.",
                true, requiresNode, new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256 },
                    ["function"] = new JsonObject { ["type"] = "string", ["maxLength"] = 80 }
                }),
            Descriptor(CallName, "Select: function cartridge_select, arguments {key: catalog key}. "
                + "After Info, run one discovered function using existing node permissions.",
                false, requiresNode, new JsonObject
                {
                    ["function"] = new JsonObject { ["type"] = "string", ["maxLength"] = 80 },
                    ["arguments"] = new JsonObject { ["type"] = "object" }
                }, ["function"]));
    }

    private static JsonObject Descriptor(string name, string description, bool readOnly, bool requiresNode, JsonObject properties,
        string[]? requiredFields = null)
    {
        properties["node"] = new JsonObject { ["type"] = "string" };
        var required = new JsonArray((requiredFields ?? []).Select(field => (JsonNode?)JsonValue.Create(field)).ToArray());
        if (requiresNode)
        {
            required.Add("node");
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            },
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = !readOnly,
                ["idempotentHint"] = readOnly,
                ["openWorldHint"] = false
            }
        };
    }
}
