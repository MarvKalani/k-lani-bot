using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Transport;

namespace KLaniBot.Cartridges;

// Use case: an administrative cartridge describes an installation the node carries out on its own machine. It is
// a fixed, reviewed contract and not a plugin format: Load accepts exactly the one known cartridge with its id,
// version, platform, locale and step list, and refuses a file that differs in any of them. The steps are code in
// the node; nothing a cartridge file supplies (commands, scripts, download URLs, privilege escalation) is ever
// executed.
public sealed record CartridgeManifest(string Id, string Version, string Sha256, JsonObject Document)
{
    // Throws InvalidDataException when the manifest is not the reviewed installation contract.
    public static CartridgeManifest Load(string path)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (!MatchesContract(document))
            throw new InvalidDataException("Unsupported or modified administrative cartridge contract.");
        if (document["steps"] is not JsonArray entries || !entries.Select(p => p!.ToString()).SequenceEqual(ReviewedSteps))
            throw new InvalidDataException("Only the fixed reviewed installation workflow may execute.");
        if (!PrivacyLabelsMatch(document["privacy_labels"]?.AsObject()))
            throw new InvalidDataException("Wrong locale-specific privacy labels.");

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sorted(document)!.ToJsonString())));
        return new(document["id"]!.ToString(), document["version"]!.ToString(), hash, document);
    }

    private static readonly string[] AllowedProperties =
    [
        "format", "id", "version", "kind", "platform", "locale", "macos_major",
        "bundle_id", "bundle_name", "description", "steps", "privacy_labels"
    ];

    private static readonly string[] ReviewedSteps =
    [
        "preflight", "install_verified_bundle", "protect_private_state", "provision_node",
        "register_login_agent", "request_apple_permissions", "verify_gui_capabilities", "export_mdm_profile"
    ];

    private const int MaximumBundleDepth = 20;
    private const int MaximumBundleEntries = 10000;
    private const long MaximumBundleBytes = 512L * 1024 * 1024;

    private static bool MatchesContract(JsonObject document)
    {
        return !document.Any(p => !AllowedProperties.Contains(p.Key))
            && document["format"]?.GetValue<int>() == 1
            && document["id"]?.ToString() == "apple-macos-27-de-install"
            && document["version"]?.ToString() == "1.0.0"
            && document["kind"]?.ToString() == "trusted_installation"
            && document["platform"]?.ToString() == "macos"
            && document["locale"]?.ToString() == "de-DE"
            && document["macos_major"]?.GetValue<int>() == 27
            && document["bundle_id"]?.ToString() == ProtocolIdentity.BundleIdentifier
            && document["bundle_name"]?.ToString() == "k-lani-bot-node.app";
    }

    // The pane names as the German macOS 27 shows them; the installer points the person to exactly these.
    private static bool PrivacyLabelsMatch(JsonObject? labels)
    {
        return labels?.Count == 2
            && labels["accessibility"]?.ToString() == "Gerätesteuerung und Datenzugriff"
            && labels["screen_capture"]?.ToString() == "Aufnahme von Bildschirm & Systemaudio";
    }

    // Keys in ordinal order, so the hash does not depend on how the file orders them.
    private static JsonNode? Sorted(JsonNode? value)
    {
        return value switch
        {
            JsonObject obj => new JsonObject(
                obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Sorted).ToArray()),
            _ => value?.DeepClone()
        };
    }
    // Throws InvalidDataException when the bundle is empty, nested too deep, or exceeds its file or size limit.
    public static string FingerprintBundle(string directory)
    {
        RejectLinks(directory);
        var entries = new List<string>();
        CollectEntries(directory, 0, entries);
        var files = entries.OrderBy(p => Path.GetRelativePath(directory, p), StringComparer.Ordinal).ToArray();
        if (files.Length is < 1 or > MaximumBundleEntries)
            throw new InvalidDataException("Bundle file limit exceeded or bundle is empty.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (string path in files)
        {
            RejectLinks(path);
            string relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            if (Directory.Exists(path))
            {
                hash.AppendData([0]);
                continue;
            }
            long length = new FileInfo(path).Length;
            total = checked(total + length);
            if (total > MaximumBundleBytes)
                throw new InvalidDataException("Bundle exceeds 512 MiB.");
            AppendFile(hash, path, length);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Throws InvalidDataException when the bundle nests too deep or has too many entries.
    private static void CollectEntries(string parent, int depth, List<string> entries)
    {
        if (depth > MaximumBundleDepth)
            throw new InvalidDataException("Bundle nesting exceeds limit.");

        foreach (string path in Directory.EnumerateFileSystemEntries(parent))
        {
            RejectLinks(path);
            entries.Add(path);
            if (entries.Count > MaximumBundleEntries)
                throw new InvalidDataException("Bundle file limit exceeded.");
            if (Directory.Exists(path))
                CollectEntries(path, depth + 1, entries);
        }
    }

    // Length first, then the content hash, so two files cannot be merged into one without changing the fingerprint.
    private static void AppendFile(IncrementalHash hash, string path, long length)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));

        using var file = File.OpenRead(path);
        hash.AppendData(SHA256.HashData(file));
    }
    // Throws UnauthorizedAccessException when a part of the path is a link.
    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Installation paths must not contain symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
