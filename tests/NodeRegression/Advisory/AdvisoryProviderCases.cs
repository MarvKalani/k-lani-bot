using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Advisory;
using KLaniBot.Common;
using KLaniBot.Guard;

// Laya is one exchangeable provider. These cases show that the rest of the node only knows the interface:
// a second provider is accepted by the same callers, a configuration chooses its provider, doubt always
// ends in "no", and the provider's name stays in the files that belong to providers.
internal static class AdvisoryProviderCases
{
    // The only source files that may name the provider: its adapter and the list of shipped providers.
    private static readonly string[] FilesThatMayNameTheProvider = ["LayaAdvisoryEvaluator.cs", "AdvisoryProviders.cs"];

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Advisory provider");
        string state = Path.Combine(temporaryRoot, "advisory-state");
        string workspace = Path.Combine(temporaryRoot, "advisory-workspace");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(workspace);

        CheckCanonicalHash(checks);
        CheckVerdicts(checks, new LaunchFixture(state, workspace));
        CheckWireForm(checks);
        CheckConfigurations(checks, state);
        CheckProviderNameStaysInItsFiles(checks);
        return checks.Passed;
    }

    // The reference value was computed with the implementation that preceded CanonicalJson. Stored macro
    // versions and calibration reports are bound to hashes made this way.
    private static void CheckCanonicalHash(Checks checks)
    {
        var sample = JsonNode.Parse("{\"b\":1,\"a\":{\"d\":[3,2,1],\"c\":\"ä<>\\\"\\\\ ✓\"},\"e\":null,\"f\":[{\"y\":2,\"x\":1}]}")!;
        checks.That(
            CanonicalJson.Hash(sample) == "10b73aa1123b55b6ef8b9fb46d5beac14860073bdbfd0623cc245ab2e5896094",
            "the canonical hash did not change");
        var reordered = JsonNode.Parse("{\"e\":null,\"f\":[{\"x\":1,\"y\":2}],\"a\":{\"c\":\"ä<>\\\"\\\\ ✓\",\"d\":[3,2,1]},\"b\":1}")!;
        checks.That(CanonicalJson.Hash(reordered) == CanonicalJson.Hash(sample), "property order does not change the hash");
    }

    // Only an explicit, calibrated "pass" lets the action run; every other answer, and a provider failure, stops it.
    private static void CheckVerdicts(Checks checks, LaunchFixture fixture)
    {
        var quietError = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            var passing = new FixedAnswerAdvisor("pass");
            checks.That(fixture.Launch(passing)["success"]?.GetValue<bool>() == true
                && fixture.Ui.LaunchCalls == 1
                && passing.Calls == 1, "a second provider can approve through the same classifier");
            checks.That(
                fixture.Launch(new FixedAnswerAdvisor("block"))["outcome"]?.ToString() == "not_dispatched" && fixture.Ui.LaunchCalls == 1,
                "a veto from any provider stops the action");
            checks.That(
                fixture.Launch(new FixedAnswerAdvisor("review"))["outcome"]?.ToString() == "not_dispatched" && fixture.Ui.LaunchCalls == 1,
                "'review' is not agreement");
            checks.That(
                fixture.Launch(new FixedAnswerAdvisor("pass", abstain: true))["outcome"]?.ToString() == "not_dispatched"
                && fixture.Ui.LaunchCalls == 1,
                "an uncalibrated 'pass' abstains and stops the action");
            checks.That(
                fixture.Launch(new FixedAnswerAdvisor("pass", success: false))["outcome"]?.ToString() == "not_dispatched"
                && fixture.Ui.LaunchCalls == 1,
                "an uncertain answer stops the action");
            var failing = new FixedAnswerAdvisor("pass") { Failure = new InvalidOperationException("provider failed") };
            checks.That(
                fixture.Launch(failing)["outcome"]?.ToString() == "not_dispatched" && fixture.Ui.LaunchCalls == 1,
                "a provider that throws stops the action");
        }
        finally
        {
            Console.SetError(quietError);
        }
    }

    private static void CheckWireForm(Checks checks)
    {
        var uncertainJson = AdvisoryResult.Uncertain("id", "fixed-answer", "revision", "rubric", "why").ToJson();

        checks.That(
            uncertainJson["provider"]!.ToString() == "fixed-answer" && uncertainJson["success"]!.GetValue<bool>() == false,
            "the wire form names the provider");
        checks.That(uncertainJson["can_override_assertions"]!.GetValue<bool>() == false
            && uncertainJson["can_grant_permissions"]!.GetValue<bool>() == false, "no answer claims authority");
    }

    private static void CheckConfigurations(Checks checks, string state)
    {
        string credential = Path.Combine(state, "advisor-credential");
        File.WriteAllText(credential, new string('x', 40));
        string unnamedFile = WriteConfiguration(state, "unnamed-advisor.json", FlatConfiguration("", credential));
        checks.That(
            ExceptionMessage(() => AdvisoryProviders.Create(unnamedFile)).Contains("Known providers", StringComparison.Ordinal),
            "a configuration must name its provider");
        string explicitFile = WriteConfiguration(state, "explicit-advisor.json", FlatConfiguration("\"provider\":\"laya\",", credential));
        using (var explicitProvider = AdvisoryProviders.Create(explicitFile))
        {
            checks.That(explicitProvider.ProviderId == "laya", "a configuration can name its provider");
        }
        string unknownFile = WriteConfiguration(
            state, "unknown-advisor.json", FlatConfiguration("\"provider\":\"not-installed\",", credential));
        checks.That(
            ExceptionMessage(() => AdvisoryProviders.Create(unknownFile))
                .Contains("advisory provider 'not-installed'", StringComparison.Ordinal),
            "an unknown provider is an error that names the known ones");
        string arrayFile = WriteConfiguration(state, "array-advisor.json", "[]");
        checks.That(
            ExceptionMessage(() => AdvisoryProviders.Create(arrayFile)).Contains("JSON object", StringComparison.Ordinal),
            "a configuration that is not an object is refused");
    }

    private static string WriteConfiguration(string state, string name, string content)
    {
        string path = Path.Combine(state, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string FlatConfiguration(string providerLine, string credential)
    {
        return "{"
            + providerLine
            + "\"enabled\":true,\"endpoint\":\""
            + "http://127.0.0.1:8099"
            + "\",\"allowLoopbackHttp\":true,\"credentialFile\":\""
            + credential.Replace("\\", "\\\\")
            + "\",\"modelRevision\":\""
            + new string('a', 40)
            + "\"}";
    }

    // Every source folder is searched; build output is not source.
    private static void CheckProviderNameStaysInItsFiles(Checks checks)
    {
        string sourceDirectory = Path.Combine(RepositoryLocator.Root(), "src", "k-lani-bot-node");
        string[] buildFolders = [Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, Path.DirectorySeparatorChar + "obj"
            + Path.DirectorySeparatorChar];
        var offenders = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !buildFolders.Any(folder => file.Contains(folder, StringComparison.Ordinal)))
            .Where(file => !FilesThatMayNameTheProvider.Contains(Path.GetFileName(file)))
            .Where(file => File.ReadAllText(file).Contains("laya", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();
        checks.That(offenders.Length == 0, "only provider files name the provider; found in: " + string.Join(", ", offenders));
    }

    // A principal that may launch one approved program under the desktop guard with a stated intent, so the classifier is asked.
    private sealed class LaunchFixture
    {
        private readonly string _application;
        private readonly NodePolicy _policy;
        private readonly RequestContext _caller = new("operator", Guid.CreateVersion7().ToString());

        public LaunchFixture(string state, string workspace)
        {
            _application = Path.Combine(workspace, "approved-program");
            var guard = new UiGuardOptions { Mode = UiGuardMode.Desktop, Intent = "Synthetic task: launch the approved program" };
            var principal = new PrincipalPolicy(
                "operator",
                new string('a', 64),
                [Scope.Observe, Scope.Process],
                [_application],
                DateTimeOffset.UtcNow.AddYears(1))
            {
                UiGuard = guard
            };
            _policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = state,
                WorkspaceRoot = workspace,
                Principals = [principal]
            };
        }

        public FakeUi Ui
        {
            get;
        } = new();

        public JsonObject Launch(IAdvisoryEvaluator advisor)
        {
            using var dispatcher = new ActionDispatcher(Ui, _policy, classifier: new NodeRiskClassifier(advisor));
            var request = new JsonObject
            {
                ["action"] = "launch_process",
                ["args"] = new JsonObject
                {
                    ["path"] = _application,
                    ["request_id"] = Guid.CreateVersion7().ToString()
                }
            };
            return dispatcher.Execute(request, _caller);
        }
    }

    private static string ExceptionMessage(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return exception.Message;
        }
        return "";
    }
}

// A second provider that always gives the answer it was constructed with.
internal sealed class FixedAnswerAdvisor(string choice, bool abstain = false, bool success = true) : IAdvisoryEvaluator
{
    public int Calls
    {
        get; private set;
    }

    public Exception? Failure
    {
        get; init;
    }

    public string ProviderId => "fixed-answer";

    public Task<AdvisoryResult> EvaluateAsync(JsonNode state, JsonObject questions, CancellationToken cancellation = default)
    {
        Calls++;
        if (Failure != null)
        {
            throw Failure;
        }
        var answers = new JsonObject
        {
            ["risk"] = new JsonObject
            {
                ["type"] = "choice",
                ["choice"] = choice
            }
        };
        var outcome = success ? AdvisoryOutcome.Advisory : AdvisoryOutcome.Uncertain;
        var result = new AdvisoryResult(
            success,
            outcome,
            Guid.CreateVersion7().ToString(),
            ProviderId,
            "revision",
            CanonicalJson.Hash(questions),
            answers,
            null,
            "fixed",
            !abstain,
            abstain,
            null);
        return Task.FromResult(result);
    }

    public void Dispose()
    {
    }
}
