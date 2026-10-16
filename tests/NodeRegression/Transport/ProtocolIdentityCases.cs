using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Common;
using KLaniBot.Startup;
using KLaniBot.Transport;

// The names in ProtocolIdentity keep their bytes unless someone changes them on purpose, and the former
// product name does not appear anywhere in the repository.
internal static class ProtocolIdentityCases
{
    // Assembled so that this file does not contain the word it searches for.
    private static readonly string FormerName = "auto" + "bot";

    public static int Run()
    {
        var checks = new Checks("Protocol identity");
        string root = RepositoryLocator.Root();

        CheckNames(checks);
        CheckSameIdentityEverywhere(checks, root);
        var offenders = new List<string>();
        foreach (string relative in TrackedFiles(root))
        {
            if (relative.Contains(FormerName, StringComparison.OrdinalIgnoreCase) || ContainsFormerName(Path.Combine(root, relative)))
            {
                offenders.Add(relative);
            }
        }
        checks.That(offenders.Count == 0, "the former product name appears in: " + string.Join(", ", offenders));
        return checks.Passed;
    }

    private static void CheckNames(Checks checks)
    {
        checks.That(ProtocolIdentity.SessionHeader == "X-K-Lani-Bot-Session", "session header");
        checks.That(ProtocolIdentity.PresentationHeader == "X-K-Lani-Bot-Presentation", "presentation header");
        checks.That(ProtocolIdentity.NodeHeader == "X-K-Lani-Bot-Node", "node header");
        checks.That(ProtocolIdentity.VersionHeader == "X-K-Lani-Bot-Version", "version header");
        checks.That(ProtocolIdentity.UpdatesHeader == "X-K-Lani-Bot-Updates", "updates header");
        checks.That(ProtocolIdentity.PairingCommitDomain == "k-lani-bot/pairing/v1/commit", "pairing commitment domain");
        checks.That(ProtocolIdentity.PairingSessionDomain == "k-lani-bot/pairing/v1/session", "pairing session domain");
        checks.That(ProtocolIdentity.FleetProofDomain == "k-lani-bot/fleet/v1", "fleet proof domain");
        checks.That(ProtocolIdentity.NodeUpdateSignatureDomain == "k-lani-bot/node-update/v1", "update signature domain");
        checks.That(ProtocolIdentity.MasterDiscoveryQuery == "k-lani-bot/master/v1", "discovery query");
        checks.That(ProtocolIdentity.MasterAdministrationChannelPrefix == "k-lani-bot-master-", "administration channel prefix");
        checks.That(ProtocolIdentity.BundleIdentifier == "com.k-lani.bot.node", "bundle identifier");
    }

    // The cartridge, the test bundle, the project and the bundle script must carry the same identifier and version.
    private static void CheckSameIdentityEverywhere(Checks checks, string root)
    {
        var cartridge = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "cartridges/apple/macos-27-de/cartridge.json")))!;
        checks.That(
            cartridge["bundle_id"]?.ToString() == ProtocolIdentity.BundleIdentifier,
            "the Apple cartridge names the same bundle identifier");
        string plist = File.ReadAllText(Path.Combine(root, "tests/Apple/Bundle/Info.plist"));
        checks.That(
            plist.Contains("<string>" + ProtocolIdentity.BundleIdentifier + "</string>", StringComparison.Ordinal),
            "the test bundle names the same bundle identifier");
        // One product version: the update channel compares it, so the project, the Mac bundle and the code must agree.
        string project = File.ReadAllText(Path.Combine(root, "src/k-lani-bot-node/k-lani-bot-node.csproj"));
        checks.That(
            project.Contains("<Version>" + ProductIdentity.DisplayVersion + "</Version>", StringComparison.Ordinal),
            "the project version is the product version");
        checks.That(
            plist.Contains("<key>CFBundleVersion</key><string>" + ProductIdentity.DisplayVersion + "</string>", StringComparison.Ordinal)
                && plist.Contains(
                    "<key>CFBundleShortVersionString</key><string>" + ProductIdentity.DisplayVersion + "</string>",
                    StringComparison.Ordinal),
            "the Mac bundle carries the product version");
        string bundleScript = File.ReadAllText(Path.Combine(root, "scripts/macos-bundle.sh"));
        checks.That(
            bundleScript.Contains("--identifier " + ProtocolIdentity.BundleIdentifier, StringComparison.Ordinal),
            "the bundle script signs with the same bundle identifier");
    }

    private static bool ContainsFormerName(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        bool binary = Array.IndexOf(content, (byte)0, 0, Math.Min(content.Length, 8192)) >= 0;
        return !binary && System.Text.Encoding.UTF8.GetString(content).Contains(FormerName, StringComparison.OrdinalIgnoreCase);
    }

    // Exactly the files of the repository, so build output and local archives are never mistaken for sources.
    private static IEnumerable<string> TrackedFiles(string root)
    {
        var listing = ProcessRunner.Run("git", ["ls-files", "-z"], 20000, root);
        if (!listing.Success)
        {
            throw new Exception("Protocol identity: git ls-files failed: " + listing.Error);
        }
        return listing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => File.Exists(Path.Combine(root, path)));
    }
}
