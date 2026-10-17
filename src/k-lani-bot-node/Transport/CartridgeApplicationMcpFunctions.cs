using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

namespace KLaniBot.Transport;

// Use case: an agent on the three-tool cartridge gateway uses the stored app MCP functions the compact gateway keeps in the same
// cartridge library. cartridge_node_status names the programs and their functions, cartridge_call saves, lists and runs them, and
// cartridge_browse {function} returns one's call schema. One instance lives as long as the gateway session, one view per node.
internal sealed class CartridgeApplicationMcpFunctions(string libraryRoot)
{
    private readonly Dictionary<string, CompactApplicationMcpFunctions> _nodes = new(StringComparer.Ordinal);

    // Adds app_mcps, the function and drift rows and the sentences that say how to use them; a node without programs is unchanged.
    // Throws what the node connection throws for cancellation or a broken transport.
    public async Task AddToStatusAsync(string node, ICompactNodeClient client, JsonNode? capabilities, JsonObject status,
        CancellationToken cancellation)
    {
        if (capabilities?["applicationMcps"] is not JsonArray { Count: > 0 } programs)
        {
            return;
        }

        CompactApplicationMcpFunctions functions = For(node, client);
        status[CompactApplicationMcpNames.Field] = programs.DeepClone();
        await functions.AddToInfoAsync(status, cancellation);
        status["next"] = Sentences(functions);
    }

    // Null when the call is none of the stored-function routes, so the cartridge routing goes on.
    // Throws what the node connection throws for cancellation or a broken transport.
    public async Task<JsonObject?> TryCallAsync(string node, ICompactNodeClient client, string function, JsonObject arguments,
        CancellationToken cancellation)
    {
        CompactApplicationMcpFunctions functions = For(node, client);

        if (function == CompactApplicationMcpFunctions.SaveFunction)
        {
            return await functions.SaveAsync(arguments, cancellation);
        }
        if (function == CompactApplicationMcpFunctions.ListFunction)
        {
            return await functions.ListAsync(arguments, cancellation);
        }
        return await functions.TryCallAsync(function, arguments, cancellation);
    }

    public void AddToListing(string node, ICompactNodeClient client, JsonObject arguments, JsonObject listing)
    {
        For(node, client).AddToListing(arguments, listing);
    }

    // The browse descriptor of a function the node's latest listing named, or null.
    public JsonObject? Describe(string node, string function)
    {
        if (!_nodes.TryGetValue(node, out CompactApplicationMcpFunctions? functions))
        {
            return null;
        }

        return functions.Describe(function);
    }

    private CompactApplicationMcpFunctions For(string node, ICompactNodeClient client)
    {
        if (!_nodes.TryGetValue(node, out CompactApplicationMcpFunctions? functions))
        {
            functions = new CompactApplicationMcpFunctions(client, libraryRoot);
            _nodes[node] = functions;
        }

        return functions;
    }

    private static string Sentences(CompactApplicationMcpFunctions functions)
    {
        var sentences = new List<string> { CompactApplicationMcpNames.Hint };
        if (functions.FunctionsListed)
        {
            sentences.Add(CompactApplicationMcpFunctionNames.FunctionsHint);
        }
        if (functions.DriftListed)
        {
            sentences.Add(CompactApplicationMcpFunctionNames.DriftHint);
        }

        return string.Join(" ", sentences);
    }
}
