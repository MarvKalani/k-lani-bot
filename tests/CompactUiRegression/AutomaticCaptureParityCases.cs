using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// First-use parity uses a synthetic library and current fingerprints; durable installation and real application acceptance remain separate.
internal static class AutomaticCaptureParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = node!.AsObject();
            var library = new Library();
            CompactObservedCompatibility current = CompatibilityParityCases.Observed(fixture["current"]!.AsObject());
            var references = fixture["references"]!.AsObject().ToDictionary(entry => entry.Key,
                entry => CaptureParityCases.Target(entry.Value!.AsObject()));
            CompactDemonstration demonstration = Demonstration(fixture);
            var automatic = new CompactAutomaticCapture(library);
            var first = automatic.Capture(demonstration, references, current, new(CompactTopicCategory.Use, "UI"),
                fixture["compact"]?.GetValue<bool>() ?? false);
            JsonObject reply = Reply(first, fixture["compact"]?.GetValue<bool>() ?? false);
            if (fixture["repeat"]?.GetValue<bool>() == true)
            {
                reply["second"] = Reply(automatic.Capture(demonstration, references, current,
                    new(CompactTopicCategory.Use, "UI"), fixture["compact"]?.GetValue<bool>() ?? false),
                    fixture["compact"]?.GetValue<bool>() ?? false);
            }
            reply["definitions"] = new JsonArray(library.Functions.Select(entry => (JsonNode?)
                CompactCaptureWire.Draft(entry.Draft)).ToArray());
            reply["candidates"] = new JsonArray(library.Candidates.Select(candidate => (JsonNode?)Candidate(candidate)).ToArray());
            output.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = reply });
        }

        return output;
    }

    private static CompactDemonstration Demonstration(JsonObject fixture)
    {
        JsonObject step = fixture["step"]!.AsObject();
        var target = CaptureParityCases.Target(step["target"]!.AsObject());
        var action = Enum.Parse<CompactRecipeAction>(step["action"]!.GetValue<string>(), true);
        CompactRecipeRoute route = action == CompactRecipeAction.Write ? CompactRecipeRoute.NativeText : CompactRecipeRoute.Semantic;

        return new(Guid.NewGuid(), 10, [new(action, target, route, step["text"]?.GetValue<string>(), step["ref"]?.GetValue<string>())],
            CaptureParityCases.Nodes(fixture["before"]!.AsArray()), CaptureParityCases.Nodes(fixture["after"]!.AsArray()));
    }

    public static JsonObject Reply(IReadOnlyList<CompactAutomaticResult> results, bool compact)
    {
        return CompactAutomaticWire.Reply(results, compact);
    }

    private static JsonObject Candidate(CompactCaptureCandidate candidate)
    {
        return new()
        {
            ["name"] = candidate.Name,
            ["topic"] = CompactCaptureNames.Topic(candidate.Topic),
            ["action"] = CompactCaptureNames.Action(candidate.Action),
            ["selector"] = CompactDiscoveryWire.Selector(candidate.Selector),
            ["reason"] = candidate.Reason
        };
    }

    // The synthetic library completes installation synchronously, so available cannot arise from mere compilation.
    private sealed class Library : ICompactCaptureLibrary
    {
        private readonly List<CompactInstalledFunction> _functions = [];
        public IReadOnlyList<CompactInstalledFunction> Functions => _functions;
        public List<CompactCaptureCandidate> Candidates { get; } = [];

        public bool TryInstall(CompactFunctionDraft draft, CompactCompatibilityContract compatibility, out string? error)
        {
            error = null;
            _functions.Add(new(draft, new(CompactCompatibilityFormat.Contracts2, Contracts: compatibility)));
            Candidates.RemoveAll(candidate => candidate.Name == draft.Name);

            return true;
        }

        public bool TryCandidate(CompactCaptureCandidate candidate, out string? error)
        {
            error = null;
            if (Candidates.All(earlier => earlier.Name != candidate.Name))
            {
                Candidates.Add(candidate);
            }

            return true;
        }
    }
}
