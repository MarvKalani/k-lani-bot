using System.Text.Json.Nodes;
using KLaniBot.Inventory;

namespace KLaniBot.Cartridges;

// How far a cartridge has been tried on the real program, from nothing to a passed run on the real application.
public enum CartridgeValidation
{
    Unreviewed,
    FixtureTested,
    ObservationTested,
    RealAppTested
}

// One cartridge, reduced to what the scanner needs to decide whether it can be used on an installed program.
public sealed record CartridgeOffer(
    string CartridgeId,
    string CartridgeVersion,
    string Application,
    ApplicationVersion? ApplicationVersion,
    ApplicationVersion? MinimumVersion,
    ApplicationVersion? MaximumVersionExclusive,
    string Platform,
    string Architecture,
    CartridgeValidation Validation,
    bool ReleaseApproved,
    string[] EntryPoints,
    string Path);

// Use case: before an operator tries to remote-control an installed program, the question is "do we already
// have a cartridge for it, for this version, for this operating system?". The catalogue reads the cartridges
// in a directory tree and nothing else: it never runs a recipe and never follows a link.
//
// Two manifest forms exist today. The local macro bundle (cartridge.json, k-lani-bot.local-macro-bundle.v1)
// names one tested application version. The package manifest (manifest.json, format 1, kind automation)
// names an application and a version range. Installation cartridges for the operating system itself are
// not about a program and are skipped.
public static class CartridgeCatalogue
{
    private const string BundleFormat = "k-lani-bot.local-macro-bundle.v1";
    private const int MaximumDepth = 8;
    private const int MaximumCartridges = 10000;

    // Throws InvalidDataException when the directory holds more cartridges than the scanner reads.
    public static IReadOnlyList<CartridgeOffer> Load(string directory)
    {
        CartridgeManifest.RejectLinks(directory);
        var offers = new List<CartridgeOffer>();
        foreach (string file in FindManifests(directory, 0))
        {
            CartridgeOffer? offer = TryRead(file);
            if (offer != null)
            {
                offers.Add(offer);
            }
            if (offers.Count > MaximumCartridges)
            {
                throw new InvalidDataException("The cartridge directory holds more cartridges than the scanner supports.");
            }
        }
        return offers.OrderBy(offer => offer.Application, StringComparer.OrdinalIgnoreCase)
            .ThenBy(offer => offer.CartridgeVersion, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> FindManifests(string directory, int depth)
    {
        if (depth > MaximumDepth)
        {
            yield break;
        }
        foreach (string file in Directory.EnumerateFiles(directory).Where(IsManifestName).Order(StringComparer.Ordinal))
        {
            yield return file;
        }
        foreach (string child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            foreach (string file in FindManifests(child, depth + 1))
            {
                yield return file;
            }
        }
    }

    private static bool IsManifestName(string file)
    {
        string name = Path.GetFileName(file);
        return name == "cartridge.json" || name == "manifest.json";
    }

    private static CartridgeOffer? TryRead(string file)
    {
        if (new FileInfo(file).Length > 1024 * 1024)
        {
            return null;
        }
        JsonObject? document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (document == null)
        {
            return null;
        }
        if (document["format"]?.ToString() == BundleFormat)
        {
            return ReadBundle(document, file);
        }
        if (document["kind"]?.ToString() == "automation" && document["target"]?["application"] is JsonObject)
        {
            return ReadPackage(document, file);
        }
        return null;
    }

    private static CartridgeOffer? ReadBundle(JsonObject document, string file)
    {
        var target = document["target"] as JsonObject;
        string? application = target?["application"]?.ToString();
        string? id = document["id"]?.ToString();
        string? version = document["version"]?.ToString();
        if (target == null || application == null || id == null || version == null)
        {
            return null;
        }
        TestedPlatform tested = SplitPlatform(target["tested_platform"]?.ToString());
        var entryPoints = document["entrypoints"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        return new CartridgeOffer(
            id,
            version,
            application,
            ApplicationVersion.Parse(target["application_version"]?.ToString()),
            null,
            null,
            tested.Platform,
            tested.Architecture,
            BestValidation(entryPoints),
            document["release_approved"]?.GetValue<bool>() == true,
            entryPoints
                .Where(entry => entry["testing_only"]?.GetValue<bool>() != true)
                .Select(entry => entry["name"]?.ToString() ?? "")
                .Where(name => name != "")
                .ToArray(),
            file);
    }

    private static CartridgeOffer? ReadPackage(JsonObject document, string file)
    {
        var target = (JsonObject)document["target"]!;
        var application = (JsonObject)target["application"]!;
        string? applicationId = application["id"]?.ToString();
        string? id = document["id"]?.ToString();
        string? version = document["version"]?.ToString();
        if (applicationId == null || id == null || version == null)
        {
            return null;
        }
        var range = application["version"] as JsonObject;
        var entryPoints = document["entrypoints"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        return new CartridgeOffer(
            id,
            version,
            applicationId,
            null,
            range?["minimum"] is { } minimum ? ApplicationVersion.Parse(minimum.ToString()) : null,
            range?["maximum_exclusive"] is { } maximum ? ApplicationVersion.Parse(maximum.ToString()) : null,
            target["platform"]?.ToString() ?? "",
            target["architecture"]?.ToString() ?? "any",
            CartridgeValidation.Unreviewed,
            false,
            entryPoints.Select(entry => entry["name"]?.ToString() ?? "").Where(name => name != "").ToArray(),
            file);
    }

    // The operating system and architecture a cartridge was tested on, split from its platform word.
    private sealed record TestedPlatform(string Platform, string Architecture);

    // "linux-x64" is operating system and processor architecture in one word.
    private static TestedPlatform SplitPlatform(string? tested)
    {
        if (string.IsNullOrEmpty(tested))
        {
            return new TestedPlatform("", "any");
        }

        int dash = tested.IndexOf('-');
        if (dash < 0)
        {
            return new TestedPlatform(tested, "any");
        }
        return new TestedPlatform(tested[..dash], tested[(dash + 1)..]);
    }

    // Entrypoints marked testing_only exercise a synthetic test double and say nothing about the real program.
    private static CartridgeValidation BestValidation(JsonObject[] entryPoints)
    {
        var best = CartridgeValidation.Unreviewed;
        foreach (var entry in entryPoints.Where(entry => entry["testing_only"]?.GetValue<bool>() != true))
        {
            var level = entry["validation"]?.ToString() switch
            {
                "real_app_tested" => CartridgeValidation.RealAppTested,
                "real_app_observation_tested" => CartridgeValidation.ObservationTested,
                "fixture_tested" => CartridgeValidation.FixtureTested,
                _ => CartridgeValidation.Unreviewed
            };
            if (level > best)
            {
                best = level;
            }
        }
        return best;
    }
}
