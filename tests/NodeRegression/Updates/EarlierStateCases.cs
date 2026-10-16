using System.Security.Cryptography;
using KLaniBot;
using KLaniBot.Audit;
using KLaniBot.Guard;
using KLaniBot.Startup;

// A node of the current version must start on the state an installed node of the oldest known version left behind, or the
// update to it fails on that machine. The fixture is the state 0.2610.0112 wrote: configuration, policies and format 1
// journals. When a reader becomes stricter, this fails before a release does, and the fix belongs in the reader.
internal static class EarlierStateCases
{
    private const string FixtureRelativePath = "tests/NodeRegression/fixtures/state-0.2610.0112";
    private const int AuditKeyBytes = 32;
    private const string FixtureSourceSuffix = ".txt";

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Earlier state");
        string state = Path.Combine(temporaryRoot, "earlier-state");
        string workspace = Path.Combine(temporaryRoot, "earlier-workspace");
        Directory.CreateDirectory(workspace);
        CopyFixture(Path.Combine(RepositoryLocator.Root(), FixtureRelativePath), state, workspace);

        NodeConfiguration configuration = NodeConfiguration.Load(Path.Combine(state, "node.json"));
        NodePolicy policy = NodePolicy.Load(configuration.PolicyPath);
        NodePolicy worker = NodePolicy.Load(Path.Combine(state, "worker-policy.json"));
        checks.That(policy.NodeId == "windows-node" && worker.Principals.Single().UiGuard.Mode == UiGuardMode.Desktop,
            "the configuration and both policies of the earlier node are read");

        string actuator = Path.Combine(state, "actuator");
        string visibility = Path.Combine(actuator, "visibility");
        checks.That(new AuditJournal(actuator).Sequence == 1 && new AuditJournal(visibility).Sequence == 1,
            "the earlier journals are set aside and new ones begin");
        checks.That(File.Exists(Path.Combine(actuator, "audit-format-1.jsonl")), "the earlier journal is kept");
        return checks.Passed;
    }

    // Paths, the worker program, test credentials and expiry are supplied only when materializing the fixture.
    private static void CopyFixture(string fixture, string state, string workspace)
    {
        string expires = DateTimeOffset.UtcNow.AddDays(30).ToString("O");
        string tokenHash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(AuditKeyBytes));
        foreach (string source in Directory.EnumerateFiles(fixture, "*" + FixtureSourceSuffix, SearchOption.AllDirectories))
        {
            // Reviewed fixtures use .txt; private runtime policy files are excluded from Git and cannot supply test input.
            string relative = Path.GetRelativePath(fixture, source);
            string destination = Path.Combine(state, relative[..^FixtureSourceSuffix.Length]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string text = File.ReadAllText(source)
                .Replace("{STATE}", state)
                .Replace("{WORKSPACE}", workspace)
                .Replace("{WORKER}", Environment.ProcessPath!)
                .Replace("{TOKEN_SHA256}", tokenHash)
                .Replace("{EXPIRES}", expires);
            File.WriteAllText(destination, text);
        }
        foreach (string journalDirectory in new[] { "actuator", Path.Combine("actuator", "visibility") })
        {
            File.WriteAllBytes(Path.Combine(state, journalDirectory, "audit.key"), RandomNumberGenerator.GetBytes(AuditKeyBytes));
        }
    }
}
