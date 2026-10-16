using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Transport.CompactUi;

namespace KLaniBot.Transport;

// One logical program/build catalog entry can hold a reviewed native bundle and one generated extension without exposing
// their full schemas.
public sealed record GatewayProgramCartridge(CompactProgramIdentity Identity, string Application, string? ProcessName,
    CartridgeOffer? Native, CompactGeneratedCatalogEntry? Generated, string Key);

// The host merges exact identities or unambiguous legacy process aliases; duplicate generated files remain uncallable rather
// than silently selected.
public sealed class GatewayProgramCatalog(string root)
{
    // Throws CompactNodeException or InvalidDataException for unsafe/bounded catalog failure; never loads native recipes during discovery.
    public IReadOnlyList<GatewayProgramCartridge> Read()
    {
        var native = NativeEntries();
        var result = native.ToDictionary(entry => entry.Identity, entry => entry);
        var groups = new CompactGeneratedCatalog(root).Read().GroupBy(entry => entry.Application);
        foreach (var group in groups)
        {
            CompactGeneratedCatalogEntry generated = group.Count() == 1 ? group.First()
                : group.First() with
                {
                    Status = CompactGeneratedCatalogStatus.Ambiguous,
                    FunctionCount = 0
                };
            var matches = NativeMatches(native, generated.Application);
            if (matches.Length == 1)
            {
                var selected = result[matches[0].Identity];
                result[selected.Identity] = selected with
                {
                    Generated = selected.Generated == null ? generated
                    : generated with
                    {
                        Status = CompactGeneratedCatalogStatus.Ambiguous,
                        FunctionCount = 0
                    }
                };
                continue;
            }
            if (matches.Length > 1)
            {
                generated = generated with
                {
                    Status = CompactGeneratedCatalogStatus.Ambiguous,
                    FunctionCount = 0
                };
            }
            var identity = generated.Application;
            result[identity] = new(identity, identity.Key, generated.ProcessName ?? identity.Key, null, generated, GeneratedKey(identity));
        }

        return Ordered(result.Values);
    }

    private static IReadOnlyList<GatewayProgramCartridge> Ordered(IEnumerable<GatewayProgramCartridge> entries)
    {
        return entries.OrderBy(entry => entry.Identity.Key, StringComparer.Ordinal)
            .ThenBy(entry => entry.Identity.Build, StringComparer.Ordinal).ToArray();
    }

    private static GatewayProgramCartridge[] NativeMatches(IReadOnlyList<GatewayProgramCartridge> entries,
        CompactProgramIdentity application)
    {
        var exact = entries.Where(entry => entry.Identity == application).ToArray();

        return exact.Length != 0 ? exact : entries.Where(entry => entry.Identity.Build == application.Build
            && entry.ProcessName == application.Key).ToArray();
    }

    // Throws ArgumentException for absent/ambiguous keys; callers keep discovery references opaque rather than treating them as paths.
    public GatewayProgramCartridge Resolve(string key)
    {
        var entries = Read();
        var matches = entries.Where(entry => entry.Key == key).ToArray();
        if (matches.Length == 0)
        {
            matches = entries.Where(entry => entry.Native != null
                && entry.Native.CartridgeId + ":" + entry.Identity.Build == key).ToArray();
        }
        if (matches.Length != 1)
        {
            throw new ArgumentException("Select one exact current program/build catalog key.");
        }

        return matches[0];
    }

    // Throws CompactNodeException when one observed variant/build maps to conflicting or uncallable generated documents.
    public CompactGeneratedCatalogEntry? Generated(CompactProgramIdentity observed)
    {
        var entries = Read();
        var exact = entries.Where(entry => entry.Identity == observed || entry.Generated?.Application == observed).ToArray();
        var matches = exact.Length != 0 ? exact : entries.Where(entry => entry.ProcessName == observed.Key
            && entry.Identity.Build == observed.Build).ToArray();
        if (matches.Length == 0)
        {
            return null;
        }
        if (matches.Length != 1 || matches[0].Generated is { Status: not CompactGeneratedCatalogStatus.Available })
        {
            throw new CompactNodeException("Generated program identity is ambiguous, invalid or has an unsupported version.");
        }

        return matches[0].Generated;
    }

    // Throws InvalidDataException for native catalog bounds and IO/JSON errors in a selected valid manifest's metadata.
    private IReadOnlyList<GatewayProgramCartridge> NativeEntries()
    {
        var offers = CartridgeCatalogue.Load(root).Where(offer => Path.GetFileName(offer.Path) == "cartridge.json")
            .GroupBy(offer => new CompactProgramIdentity(offer.CartridgeId, offer.ApplicationVersion?.Raw ?? ""));
        var entries = new List<GatewayProgramCartridge>();
        foreach (var group in offers)
        {
            var selected = group.OrderByDescending(offer => Revision(offer.CartridgeVersion))
                .ThenBy(offer => offer.Path, StringComparer.Ordinal).First();
            JsonNode? document = JsonNode.Parse(File.ReadAllBytes(selected.Path));
            string? process = document?["target"]?["process_name"]?.ToString();
            entries.Add(new(group.Key, selected.Application, process, WithRecipeNames(selected), null, GeneratedKey(group.Key)));
        }

        return entries;
    }

    // A function is called by the name its recipe is registered under, which may differ from its manifest entry name.
    // A bundle whose recipes cannot be read keeps the manifest names; it then lists no function in ui_info.
    private static CartridgeOffer WithRecipeNames(CartridgeOffer offer)
    {
        if (!GatewayBundleFunctions.TryRead(offer.Path, out var functions))
        {
            return offer;
        }

        return offer with
        {
            EntryPoints = functions.Select(function => function.Name).ToArray()
        };
    }

    private static Version Revision(string value)
    {
        return Version.TryParse(value, out var revision) ? revision : new(0, 0);
    }

    public static string GeneratedKey(CompactProgramIdentity identity)
    {
        return "program:" + CompactFormFields.Fingerprint(new JsonObject { ["key"] = identity.Key, ["build"] = identity.Build });
    }

    public static JsonObject Describe(GatewayProgramCartridge entry)
    {
        return new JsonObject
        {
            ["key"] = entry.Key,
            ["application"] = entry.Application,
            ["build"] = entry.Identity.Build,
            ["cartridge_version"] = entry.Native?.CartridgeVersion,
            ["generated_revision"] = entry.Generated?.Revision,
            ["platform"] = entry.Native == null ? null : entry.Native.Platform + "-" + entry.Native.Architecture,
            ["function_count"] = (entry.Native?.EntryPoints.Length ?? 0) + (entry.Generated?.FunctionCount ?? 0),
            ["generated_status"] = entry.Generated?.Status.ToString(),
            ["best_validation"] = entry.Native?.Validation.ToString(),
            ["release_approved"] = entry.Native?.ReleaseApproved ?? false
        };
    }
}
