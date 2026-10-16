using System.Text.Json.Nodes;

namespace KLaniBot.Advisory;

// How one kind of advisory provider is created from its configuration file.
public sealed record AdvisoryProviderDescriptor(string Id, Func<JsonObject, HttpMessageHandler?, IAdvisoryEvaluator> Create);

// Use case: the operator names a configuration file (--advisor-config, or the guard classifier in the node
// policy) and the node needs an advisor from it, without the caller knowing which product is behind it.
//
// This is the one place that lists the providers the node ships with, and so the only file besides the
// provider's own where its name appears; a test enforces that. To connect another product: implement
// IAdvisoryEvaluator, honour the rules stated there, and add one line to Known.
public static class AdvisoryProviders
{
    public const string OptionName = "--advisor-config";

    private static readonly AdvisoryProviderDescriptor[] Known =
    [
        new(
            LayaAdvisoryEvaluator.Id,
            (document, testingHandler) => new LayaAdvisoryEvaluator(LayaAdvisoryConfig.FromJson(document), testingHandler))
    ];

    public static IReadOnlyList<string> KnownIds => Known.Select(provider => provider.Id).ToArray();

    // Throws InvalidDataException when the file is no JSON object or names no known provider.
    public static IAdvisoryEvaluator Create(string configurationFile, HttpMessageHandler? testingHandler = null)
    {
        var document = JsonNode.Parse(File.ReadAllText(configurationFile)) as JsonObject
            ?? throw new InvalidDataException("The advisor configuration must be a JSON object.");
        // The file names its provider. Guessing one would send the operator's questions to a service they did not choose.
        string providerId = document["provider"]?.ToString() ?? "";
        var descriptor = Known.FirstOrDefault(provider => provider.Id == providerId)
            ?? throw new InvalidDataException(
                "Unknown or missing advisory provider '" + providerId + "'. Known providers: " + string.Join(", ", KnownIds) + ".");
        return descriptor.Create(document, testingHandler);
    }
}
