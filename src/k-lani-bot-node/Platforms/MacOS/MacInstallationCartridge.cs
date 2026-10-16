using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using KLaniBot.Actions;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Startup;
using KLaniBot.Transport;

namespace KLaniBot.Platforms.MacOS;

// Use case: an operator installs the node on a Mac (the German macOS 27 cartridge) for the logged-in user:
// plan shows what would happen, apply installs a verified app bundle and a login agent, verify checks a running
// installation, permissions asks the running node to request its privacy permissions, stop ends the agent. The
// node name is --node, or the name of an existing installation, or this computer's name.
//
// It runs as the user and refuses root, because the node is a GUI service that needs that user's desktop. The
// bundle must carry the expected identity and a valid code signature before it is copied; a failed update puts
// the previous bundle back. Privacy permissions (TCC) cannot be granted silently: the running node asks for them
// under its installed code identity, and for managed Macs an MDM privacy profile can be exported instead. A
// receipt records what this cartridge owns, so stop and verify never touch files it did not install.
public static partial class MacInstallationCartridge
{
    private const string BundleId = ProtocolIdentity.BundleIdentifier;
    private const string BundleName = "k-lani-bot-node.app";
    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    // Throws InvalidOperationException when the program fails.
    private static string Run(string executable, params string[] args)
    {
        var result = ProcessRunner.Run(executable, args, 30000);
        if (!result.Success)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {result.Error.Trim()}");
        return result.Output.Trim();
    }
    // Throws PlatformNotSupportedException off macOS 27 in German, ArgumentException for missing or invalid options,
    // UnauthorizedAccessException for root, and InvalidDataException for an unreadable cartridge. Failures of the action itself
    // are reported in the JSON answer.
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Apple installation cartridges run on macOS, not iOS or Linux.");

        InstallationRun run = InstallationRun.Prepare(arguments);
        return await run.ExecuteAsync();
    }

    private static string? Option(string[] arguments, string key)
    {
        int i = Array.IndexOf(arguments, key);
        return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : null;
    }

    // Where the cartridge installs: the app bundle, the private data root with state and workspace, and the login agent.
    private sealed record InstallationPaths(
        string InstallRoot,
        string DataRoot,
        string Bundle,
        string State,
        string Workspace,
        string Agent,
        string Executable,
        string NodeConfig,
        string Registry,
        string ReceiptPath)
    {
        public static InstallationPaths From(string[] arguments)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // On macOS 27 System Settings could not resolve the app in ~/Applications (mode 0700) to its privacy record
            // and hid it from the Accessibility and Screen Recording lists. In /Applications with normal modes it was
            // listed; which of the two mattered was not isolated, so both are the default.
            string installRoot = Path.GetFullPath(Option(arguments, "--install-root") ?? "/Applications");
            string dataRoot = Path.GetFullPath(
                Option(arguments, "--data-root") ?? Path.Combine(home, "Library", "Application Support", "k-lani-bot-node"));
            string bundle = Path.Combine(installRoot, BundleName);
            string state = Path.Combine(dataRoot, "state");
            return new InstallationPaths(
                installRoot,
                dataRoot,
                bundle,
                state,
                Path.Combine(dataRoot, "workspace"),
                Path.Combine(home, "Library", "LaunchAgents", ProductIdentity.NodeName + ".plist"),
                Path.Combine(bundle, "Contents", "MacOS", "k-lani-bot-node"),
                Path.Combine(state, "node.json"),
                Path.Combine(state, "nodes.json"),
                Path.Combine(dataRoot, "installation.json"));
        }
    }

    // The checked operating system: version, build and MDM enrollment as reported by macOS.
    private sealed record SystemFacts(string Version, string Build, string Enrollment);

    // The approved source of an apply: its path, the fingerprint the operator pinned and an optional signing identity.
    private sealed record ApprovedSource(string Path, string Fingerprint, string? SigningIdentity);
    // The administrator's choice wins. An installed node keeps the name it was set up with, because its registry and
    // a master know it by that name; a new installation suggests this computer's name.
    // Throws ArgumentException when nothing is requested and the computer name gives no usable node name.
    private static string ChooseNodeName(string? requested, string nodeConfig)
    {
        if (requested != null)
            return NodeName.Require(requested);
        if (File.Exists(nodeConfig))
        {
            CartridgeManifest.RejectLinks(nodeConfig);
            return NodePolicy.Load(NodeConfiguration.Load(nodeConfig).PolicyPath).NodeId;
        }
        return NodeName.SuggestForThisComputer()
            ?? throw new ArgumentException("--node required: this computer's name gives no usable node name.");
    }
    // Throws PlatformNotSupportedException outside macOS.
    private static void PrivateMode(string path, bool directory)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        File.SetUnixFileMode(
            path,
            directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static void PrivateText(string path, string text)
    {
        CartridgeManifest.RejectLinks(path);
        string temporary = path + "." + Guid.CreateVersion7() + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                PrivateMode(temporary, false);
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void PrivateJson(string path, JsonObject document)
        => PrivateText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    // Throws InvalidDataException for a wrong bundle identity or entry point.
    private static void VerifyBundle(string bundle)
    {
        CartridgeManifest.RejectLinks(bundle);
        string infoPlist = Path.Combine(bundle, "Contents", "Info.plist");
        bool identityMatches = Directory.Exists(bundle)
            && Run("/usr/bin/plutil", "-extract", "CFBundleIdentifier", "raw", "-o", "-", infoPlist) == BundleId
            && Run("/usr/bin/plutil", "-extract", "CFBundleExecutable", "raw", "-o", "-", infoPlist) == "k-lani-bot-node";
        if (!identityMatches)
            throw new InvalidDataException("Wrong bundle identity or entry point.");
        Run("/usr/bin/codesign", "--verify", "--strict", bundle);
    }
    // Throws InvalidDataException when the code requirement cannot be read.
    private static string CodeRequirement(string bundle)
    {
        var result = ProcessRunner.Run("/usr/bin/codesign", ["-d", "-r-", bundle]);
        if (!result.Success)
            throw new InvalidDataException("Code requirement unavailable.");
        string[] outputLines = (result.Error + "\n" + result.Output).Split('\n');
        string line = outputLines.SingleOrDefault(s =>
                s.StartsWith("designated => ", StringComparison.Ordinal)
                || s.StartsWith("# designated => ", StringComparison.Ordinal))
            ?? throw new InvalidDataException("Missing designated code requirement.");
        return line[(line.IndexOf("=> ", StringComparison.Ordinal) + 3)..].Trim();
    }
    // Throws IOException when the target already exists.
    private static void CopyBundle(string source, string target)
    {
        if (Directory.Exists(target))
            throw new IOException("Existing installation staging area preserved.");
        // Directories take the source's modes like files do. The installer runs with umask 077 for the private state;
        // without this the bundle's directories came out 0700, unlike any other application (see installRoot).
        Directory.CreateDirectory(target);
        if (OperatingSystem.IsMacOS())
            File.SetUnixFileMode(target, File.GetUnixFileMode(source));
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            CartridgeManifest.RejectLinks(directory);
            string created = Path.Combine(target, Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(created);
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(created, File.GetUnixFileMode(directory));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CartridgeManifest.RejectLinks(file);
            string copied = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, copied, false);
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(copied, File.GetUnixFileMode(file));
        }
    }
    // Throws UnauthorizedAccessException when the receipt does not prove this cartridge installed it.
    private static JsonObject RequireReceipt(string path, string bundle, string data, string agent, CartridgeManifest manifest)
    {
        CartridgeManifest.RejectLinks(path);
        var receipt = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (receipt["format"]?.GetValue<int>() != 1
            || receipt["cartridge"]?.ToString() != manifest.Id
            || receipt["cartridge_sha256"]?.ToString() != manifest.Sha256
            || receipt["bundle"]?.ToString() != bundle
            || receipt["data_root"]?.ToString() != data
            || receipt["launch_agent"]?.ToString() != agent
            || receipt["uid"]?.GetValue<uint>() != GetUid())
            throw new UnauthorizedAccessException("Installation is not owned by this cartridge/path/version/user.");
        return receipt;
    }
    // Throws UnauthorizedAccessException when the launch agent is not the one this cartridge wrote.
    private static void StopAgent(uint uid, string agent, JsonObject receipt)
    {
        if (!File.Exists(agent))
            return;
        var expected = AgentPlist(
            Path.Combine(receipt["bundle"]!.ToString(), "Contents", "MacOS", "k-lani-bot-node"),
            Path.Combine(receipt["data_root"]!.ToString(), "state", "node.json"),
            Path.Combine(receipt["data_root"]!.ToString(), "state"));
        using var reader = System.Xml.XmlReader.Create(
            agent,
            new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = 65536
            });
        string receiptBundle = receipt["bundle"]!.ToString();
        string receiptDataRoot = receipt["data_root"]!.ToString();
        string receiptExecutable = Path.Combine(receiptBundle, "Contents", "MacOS", "k-lani-bot-node");
        string receiptState = Path.Combine(receiptDataRoot, "state");
        bool ownedAgent = MacLoginAgentContract.MatchesOwned(
            XDocument.Load(reader).Root!, receiptExecutable, Path.Combine(receiptState, "node.json"), receiptState);
        if (!ownedAgent)
            throw new UnauthorizedAccessException("Launch agent was modified; refusing to control an unrelated service.");
        // The owned job can already be stopped. A subsequent bootstrap remains authoritative.
        ProcessRunner.Run("/bin/launchctl", ["bootout", "gui/" + uid, agent], 10000);
    }
    public static XDocument AgentPlist(string executable, string config, string state)
        => MacLoginAgentContract.Create(executable, config, state);

    // The running node may be newer than this installer and report features it does not know; those are left out.
    private static HashSet<ToolFeature> ReportedFeatures(JsonArray words)
    {
        var features = new HashSet<ToolFeature>();
        foreach (JsonNode? word in words)
        {
            if (WireNames<ToolFeature>.TryParse(word?.ToString(), out ToolFeature feature))
            {
                features.Add(feature);
            }
        }
        return features;
    }

    private static IEnumerable<XElement> Pair(string key, string value) => [new XElement("key", key), new XElement("string", value)];
    private static XDocument Plist(XElement value)
        => new(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), value));
    private static void RequestPermissions(string bundle, string root)
    {
        // Private operator mailbox, outside the agent workspace. The running
        // GUI service requests TCC using its actual installed code identity.
        PrivateJson(
            Path.Combine(root, "state", "permissions-request.json"),
            new JsonObject { ["version"] = 1, ["request_id"] = Guid.CreateVersion7().ToString() });
    }
    private static void ExportPppc(string bundle, string path)
    {
        string requirement = CodeRequirement(bundle);
        XElement Identity(string authorization)
            => new(
                "dict",
                Pair("Identifier", BundleId),
                Pair("IdentifierType", "bundleID"),
                Pair("CodeRequirement", requirement),
                Pair("Authorization", authorization));
        var services = new XElement(
            "dict",
            new XElement("key", "Accessibility"),
            new XElement("array", Identity("Allow")),
            new XElement("key", "ScreenCapture"),
            new XElement("array", Identity("AllowStandardUserToSetSystemService")));
        var payload = new XElement(
            "dict",
            Pair("PayloadType", "com.apple.TCC.configuration-profile-policy"),
            Pair("PayloadIdentifier", BundleId + ".pppc"),
            Pair("PayloadUUID", Guid.CreateVersion7().ToString()),
            new XElement("key", "PayloadVersion"),
            new XElement("integer", 1),
            new XElement("key", "Services"),
            services);
        var profile = Plist(new XElement(
            "dict",
            Pair("PayloadType", "Configuration"),
            Pair("PayloadIdentifier", BundleId + ".privacy"),
            Pair("PayloadDisplayName", "k-lani-bot-node – Datenschutz"),
            Pair("PayloadScope", "System"),
            Pair("PayloadUUID", Guid.CreateVersion7().ToString()),
            new XElement("key", "PayloadVersion"),
            new XElement("integer", 1),
            new XElement("key", "PayloadContent"),
            new XElement("array", payload)));
        PrivateText(path, profile.ToString());
    }
}
