using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Automatic capture reports availability only after installation and checks compatibility before reusing an existing function.
internal static class AutomaticCaptureCases
{
    private static readonly CompactCartridgeTopic Topic = new(CompactTopicCategory.Use, "UI");

    public static void Run()
    {
        var target = new CompactSemanticTarget(10, "Text", CompactRole.Parse("Edit"), "text", CompactWindowKind.Main, []);
        CompactDemonstration demonstration = Demonstration(target);
        var references = new Dictionary<string, CompactSemanticTarget> { ["field"] = target };
        CompactObservedCompatibility current = Current();
        var library = new Library();
        var capture = new CompactAutomaticCapture(library);
        var first = capture.Capture(demonstration, references, current, Topic, false);
        Require(first.Single().Verified && library.Functions.Count == 1, "available after actual installation");
        var patch = capture.Capture(demonstration, references, current with
        {
            NodeVersion = "1.0.2"
        }, Topic, true);
        Require(patch.Count == 0 && library.Installs == 1, "patch reuse has no duplicate function or compact noise");
        var changed = capture.Capture(demonstration, references, current with
        {
            Native = new(new string('d', 64))
        }, Topic, false);
        Require(changed.Single().Status == CompactAutomaticStatus.NeedsVerification && library.Installs == 1,
            "changed writer cannot silently approve reuse");
        FailureAndAuthority(demonstration, references, current, target);
        UnknownIdentity(demonstration, references, current);
    }

    private static void FailureAndAuthority(CompactDemonstration demonstration, Dictionary<string, CompactSemanticTarget> references,
        CompactObservedCompatibility current, CompactSemanticTarget target)
    {
        var library = new Library { FailInstall = true, FailCandidate = true };
        var capture = new CompactAutomaticCapture(library);
        var failed = capture.Capture(demonstration, references, current, Topic, false);
        Require(failed.Single().Status == CompactAutomaticStatus.StorageFailed && !failed.Single().Verified
            && library.Functions.Count == 0, "storage failure never becomes available");
        var foreign = demonstration with
        {
            Steps = [demonstration.Steps[0] with { Target = target with { ProcessId = 11 } }]
        };
        var refused = capture.Capture(foreign, references, current, Topic, false);
        Require(refused.Single().Status == CompactAutomaticStatus.NeedsVerification && library.CandidateWrites == 1,
            "foreign scope does not reach storage");
    }

    private static void UnknownIdentity(CompactDemonstration demonstration, Dictionary<string, CompactSemanticTarget> references,
        CompactObservedCompatibility current)
    {
        var library = new Library();
        var capture = new CompactAutomaticCapture(library);
        var unknown = capture.Capture(demonstration, references, current with
        {
            Platform = "unknown"
        }, Topic, false);
        Require(unknown.Single().Status == CompactAutomaticStatus.NeedsVerification && library.Installs == 0,
            "unknown identity cannot be installed even with plausible hashes");
        var missing = capture.Capture(demonstration, references, current with
        {
            Native = null
        }, Topic, false);
        Require(missing.Single().Status == CompactAutomaticStatus.NeedsVerification && library.Installs == 0,
            "missing implementation remains unknown");
    }

    private static CompactDemonstration Demonstration(CompactSemanticTarget target)
    {
        CompactTreeNode[] before = [new(0, 1, CompactRole.Parse("Window"), "Fixture", "", "", true, false),
            new(2, 2, CompactRole.Parse("Edit"), "Text", "text", "old", true, false)];
        CompactTreeNode[] after = [before[0], before[1] with { Value = "synthetic new input" }];

        return new(Guid.NewGuid(), 10, [new(CompactRecipeAction.Write, target, CompactRecipeRoute.NativeText,
            "synthetic new input", "field")], before, after);
    }

    private static CompactObservedCompatibility Current()
    {
        var schemas = new CompactActionSchemas(new Dictionary<Tool, CompactContractDigest>
        {
            [Tool.GetMachineCapabilities] = new(new string('a', 64)),
            [Tool.ListWindows] = new(new string('a', 64)),
            [Tool.GetUiTree] = new(new string('a', 64)),
            [Tool.TypeText] = new(new string('a', 64))
        }, new Dictionary<string, CompactContractDigest>());

        return new("1.0.1", CompactCompatibilityWire.Wire("2025-06-18"), "fixture-platform", new("fixture", "1.0"),
            schemas, new(new string('b', 64)), new(new string('c', 64)));
    }

    // A controlled library counts writes and can fail both phases without persisting synthetic input values.
    private sealed class Library : ICompactCaptureLibrary
    {
        private readonly List<CompactInstalledFunction> _functions = [];
        public IReadOnlyList<CompactInstalledFunction> Functions => _functions;
        public int Installs
        {
            get; private set;
        }
        public int CandidateWrites
        {
            get; private set;
        }
        public bool FailInstall
        {
            get; set;
        }
        public bool FailCandidate
        {
            get; set;
        }

        public bool TryInstall(CompactFunctionDraft draft, CompactCompatibilityContract compatibility, out string? error)
        {
            error = FailInstall ? "Synthetic installation failure." : null;
            if (FailInstall)
            {
                return false;
            }
            Installs++;
            _functions.Add(new(draft, new(CompactCompatibilityFormat.Contracts2, Contracts: compatibility)));

            return true;
        }

        public bool TryCandidate(CompactCaptureCandidate candidate, out string? error)
        {
            CandidateWrites++;
            error = FailCandidate ? "Synthetic candidate storage failure." : null;

            return !FailCandidate;
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Automatic capture regression failed: " + name);
        }
    }
}
