using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Automatic capture installs one generated extension per application/build through the actual atomic revision store.
public sealed class CompactFunctionLibrary : ICompactCaptureLibrary
{
    private readonly CompactRevisionStore _store;
    private readonly CompactProgramIdentity _application;
    private CompactFunctionSnapshot _snapshot;
    public IReadOnlyList<CompactInstalledFunction> Functions => _snapshot.Document?.Functions
        .Select(function => new CompactInstalledFunction(function.Draft, function.Compatibility)).ToArray() ?? [];
    public IReadOnlyList<CompactCaptureCandidate> Candidates => _snapshot.Document?.Candidates ?? [];
    public long Revision => _snapshot.Revision;
    public string? Digest => _snapshot.Digest;
    public int SourceVersion => _snapshot.Document?.Version ?? 2;

    private CompactFunctionLibrary(CompactRevisionStore store, CompactProgramIdentity application, CompactFunctionSnapshot snapshot)
    {
        _store = store;
        _application = application;
        _snapshot = snapshot;
    }

    public static bool TryOpen(string file, string recoveryRoot, CompactProgramIdentity application,
        out CompactFunctionAttempt<CompactFunctionLibrary> attempt)
    {
        if (!CompactRevisionStore.TryOpen(file, recoveryRoot, application, out var store))
        {
            attempt = new(null, store.Error, store.Detail);
            return false;
        }
        if (!store.Value!.TryRead(out var snapshot))
        {
            attempt = new(null, snapshot.Error, snapshot.Detail);
            return false;
        }
        attempt = new(new(store.Value, application, snapshot.Value!));

        return true;
    }

    public bool TryReload(out CompactFunctionAttempt<CompactFunctionSnapshot> attempt)
    {
        if (!_store.TryRead(out attempt))
        {
            return false;
        }
        _snapshot = attempt.Value!;

        return true;
    }

    public bool TryGet(string name, out CompactFunctionAttempt<CompactLoadedFunction> attempt)
    {
        CompactLoadedFunction? function = _snapshot.Document?.Functions.FirstOrDefault(entry => entry.Draft.Name == name);
        if (function == null)
        {
            attempt = new(null, CompactFunctionError.InvalidDefinition);
            return false;
        }
        attempt = new(function);

        return true;
    }

    public bool TryReplaceCompatibility(string name, long expectedRevision, CompactAcceptedCompatibility accepted, out string? error)
    {
        if (expectedRevision != _snapshot.Revision || !TryGet(name, out var function))
        {
            error = CompactFunctionNames.Error(CompactFunctionError.StaleRevision);
            return false;
        }
        CompactCompatibilityContract contract = accepted.Contract;
        if (contract.Application != _application || contract.FunctionScope != CompactContractHash.Function(function.Value!.Draft)
            || contract.SelectorScope != CompactContractHash.Selectors(function.Value.Draft))
        {
            error = CompactFunctionNames.Error(CompactFunctionError.InvalidDefinition);
            return false;
        }
        JsonObject document = Document();
        JsonObject selected = document["functions"]!.AsArray().Single(entry => entry!["name"]!.GetValue<string>() == name)!.AsObject();
        selected["compatibility"] = CompactCompatibilityWire.Contract(contract with
        {
            Acceptance = accepted.Reference.Value
        });

        return TrySave(document, out error);
    }

    public bool TryInstall(CompactFunctionDraft draft, CompactCompatibilityContract compatibility, out string? error)
    {
        if (compatibility.Application != _application || compatibility.FunctionScope != CompactContractHash.Function(draft)
            || compatibility.SelectorScope != CompactContractHash.Selectors(draft))
        {
            error = CompactFunctionNames.Error(CompactFunctionError.InvalidDefinition);
            return false;
        }
        if (Functions.Any(entry => entry.Draft.Name == draft.Name))
        {
            error = CompactFunctionNames.Error(CompactFunctionError.AlreadyExists);
            return false;
        }
        JsonObject document = Document();
        JsonObject entry = CompactCaptureWire.Draft(draft);
        entry["compatibility"] = CompactCompatibilityWire.Contract(compatibility);
        document["functions"]!.AsArray().Add(entry);
        var candidates = document["candidates"]!.AsArray();
        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            if (candidates[index]!["name"]!.GetValue<string>() == draft.Name)
            {
                candidates.RemoveAt(index);
            }
        }

        return TrySave(document, out error);
    }

    public bool TryCandidate(CompactCaptureCandidate candidate, out string? error)
    {
        if (Candidates.Any(earlier => earlier.Name == candidate.Name))
        {
            error = null;
            return true;
        }
        JsonObject document = Document();
        document["candidates"]!.AsArray().Add(new JsonObject
        {
            ["name"] = candidate.Name,
            ["topic"] = CompactCaptureNames.Topic(candidate.Topic),
            ["action"] = CompactCaptureNames.Action(candidate.Action),
            ["selector"] = CompactDiscoveryWire.Selector(candidate.Selector),
            ["reason"] = candidate.Reason
        });

        return TrySave(document, out error);
    }

    private JsonObject Document()
    {
        JsonObject document = _snapshot.Document?.Source.DeepClone().AsObject() ?? new JsonObject
        {
            ["format"] = CompactFunctionDocumentCodec.Format,
            ["application"] = new JsonObject { ["key"] = _application.Key, ["version"] = _application.Build },
            ["functions"] = new JsonArray(),
            ["candidates"] = new JsonArray()
        };
        document["version"] = 2;
        if (!document.ContainsKey("candidates"))
        {
            document["candidates"] = new JsonArray();
        }

        return document;
    }

    private bool TrySave(JsonObject document, out string? error)
    {
        if (!_store.TryReplace(document, _snapshot.Revision, _snapshot.Digest, out var saved))
        {
            error = CompactFunctionNames.Error(saved.Error!.Value, saved.Detail);
            return false;
        }
        _snapshot = saved.Value!;
        error = null;

        return true;
    }
}
