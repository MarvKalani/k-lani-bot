using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KLaniBot.Actions;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport;

// Coverage separates reference requirements, implemented recipes, current access and real-application acceptance.
// A discovered native member is never automatically counted as an executable or accepted function.
[JsonConverter(typeof(JsonStringEnumConverter<CoverageImplementation>))]
public enum CoverageImplementation
{
    Missing,
    Partial,
    Implemented
}

// The source of a requirement determines whether it describes a workflow, native member or UI context.
[JsonConverter(typeof(JsonStringEnumConverter<CoverageRequirementKind>))]
public enum CoverageRequirementKind
{
    Workflow,
    Method,
    Property,
    Event,
    UiContext
}

// The host retains the complete requirement list; MCP returns selected topic pages and summaries only.
public sealed record CoverageRequirement(string Id, string Topic, string Name, CoverageRequirementKind Kind,
    bool Required, string Source, CoverageImplementation Implementation, string[] Recipes, string Note);

// Reference completeness is explicit: documentation, the installed API, UI contexts and token measurement need separate evidence.
public sealed record CoverageDocument(string Format, bool ReferenceComplete, string[] PendingReviews, CoverageRequirement[] Requirements);

// A pinned coverage file can be read independently of application access, so missing and blocked functions remain visible.
public sealed class CartridgeCoverage
{
    private const int PageSize = 20;
    private readonly CoverageDocument _document;

    // Throws InvalidDataException when the coverage data has invalid topics, duplicate requirements or inconsistent implementation claims.
    public CartridgeCoverage(byte[] bytes, IReadOnlyList<GatewayRecipe> recipes)
    {
        _document = JsonSerializer.Deserialize<CoverageDocument>(bytes)
            ?? throw new InvalidDataException("Coverage data is missing.");
        if (_document.Format != "k-lani-bot.cartridge-coverage.v1"
            || _document.Requirements.Select(requirement => requirement.Id).Distinct().Count() != _document.Requirements.Length)
        {
            throw new InvalidDataException("Unsupported coverage format or duplicate requirements.");
        }
        HashSet<string> known = recipes.Select(recipe => recipe.Macro.Name).ToHashSet(StringComparer.Ordinal);
        foreach (CoverageRequirement requirement in _document.Requirements)
        {
            CartridgeTopic.Parse(requirement.Topic);
            if (requirement.Recipes.Any(name => !known.Contains(name))
                || (requirement.Implementation != CoverageImplementation.Missing && requirement.Recipes.Length == 0)
                || (requirement.Implementation == CoverageImplementation.Missing && requirement.Recipes.Length != 0))
            {
                throw new InvalidDataException("Coverage refers to absent recipes or contradicts its implementation status.");
            }
        }
    }

    public JsonObject Summary(IReadOnlyList<GatewayRecipe> recipes, IReadOnlySet<Tool> available)
    {
        CoverageRequirement[] required = _document.Requirements.Where(requirement => requirement.Required).ToArray();
        int accepted = recipes.Count(recipe => recipe.Validation == CartridgeValidation.RealAppTested);
        bool complete = _document.ReferenceComplete && _document.PendingReviews.Length == 0 && required.Length > 0
            && required.All(requirement => requirement.Implementation == CoverageImplementation.Implemented)
            && accepted == recipes.Count;

        return new JsonObject
        {
            ["reference_complete"] = _document.ReferenceComplete,
            ["pending_reviews"] = new JsonArray(_document.PendingReviews.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["required_reference_items"] = required.Length,
            ["implemented_items"] = required.Count(requirement => requirement.Implementation == CoverageImplementation.Implemented),
            ["partial_items"] = required.Count(requirement => requirement.Implementation == CoverageImplementation.Partial),
            ["missing_items"] = required.Count(requirement => requirement.Implementation == CoverageImplementation.Missing),
            ["recipe_count"] = recipes.Count,
            ["available_recipe_count"] = recipes.Count(recipe => recipe.Tools.All(available.Contains)),
            ["real_app_tested_recipe_count"] = accepted,
            ["fully_accepted"] = complete,
            ["detail_path"] = "Info/Coverage"
        };
    }

    // Throws ArgumentException for an invalid detail path or page offset.
    public JsonObject Browse(string path, IReadOnlyList<GatewayRecipe> recipes, IReadOnlySet<Tool> available)
    {
        string suffix = path["Info/Coverage".Length..].TrimStart('/');
        int offset = ReadOffset(ref suffix);
        if (suffix.Length > 0)
        {
            CartridgeTopic.Parse(suffix);
        }
        CoverageRequirement[] requirements = _document.Requirements.Where(requirement => suffix.Length == 0
            || requirement.Topic == suffix || requirement.Topic.StartsWith(suffix + "/", StringComparison.Ordinal)).ToArray();
        var byName = recipes.ToDictionary(recipe => recipe.Macro.Name, StringComparer.Ordinal);
        var page = new JsonArray(requirements.Skip(offset).Take(PageSize).Select(requirement =>
            Describe(requirement, byName, available)).ToArray());
        string root = "Info/Coverage" + (suffix.Length == 0 ? "" : "/" + suffix);

        return new JsonObject
        {
            ["success"] = true,
            ["summary"] = Summary(recipes, available),
            ["topic"] = suffix,
            ["total"] = requirements.Length,
            ["next_path"] = offset + PageSize < requirements.Length ? root + "@" + (offset + PageSize) : null,
            ["requirements"] = page
        };
    }

    // Throws ArgumentException for a malformed page offset.
    private static int ReadOffset(ref string topic)
    {
        int separator = topic.LastIndexOf('@');
        if (separator < 0)
        {
            return 0;
        }
        if (!int.TryParse(topic[(separator + 1)..], out int offset) || offset < 0)
        {
            throw new ArgumentException("Coverage pages require a nonnegative offset.");
        }
        topic = topic[..separator];

        return offset;
    }

    private static JsonNode Describe(CoverageRequirement requirement, IReadOnlyDictionary<string, GatewayRecipe> recipes,
        IReadOnlySet<Tool> available)
    {
        GatewayRecipe[] mapped = requirement.Recipes.Select(name => recipes[name]).ToArray();

        return new JsonObject
        {
            ["id"] = requirement.Id,
            ["topic"] = requirement.Topic,
            ["name"] = requirement.Name,
            ["kind"] = requirement.Kind.ToString(),
            ["required"] = requirement.Required,
            ["implementation"] = requirement.Implementation.ToString(),
            ["available"] = mapped.Length > 0 && mapped.All(recipe => recipe.Tools.All(available.Contains)),
            ["acceptance"] = mapped.Length > 0 ? mapped.Min(recipe => recipe.Validation).ToString() : "Unreviewed",
            ["recipes"] = new JsonArray(requirement.Recipes.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["source"] = requirement.Source,
            ["note"] = requirement.Note
        };
    }
}
