using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Public embedded descriptors must preserve the JS reference's field/type bounds and exact requested discovery scope.
internal static class CompactDescriptorParityCases
{
    public static JsonObject Run()
    {
        return new JsonObject
        {
            ["tools"] = CompactGatewayTools.Descriptors(),
            ["functions"] = CompactGatewayFunctionTools.All.DeepClone()
        };
    }
}
