using System.Text.Json.Nodes;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport;

// A controller exposes a paged local library. Discovery reads metadata only; selecting a known key loads its pinned
// recipes. A newly installed bundle is discovered on the next catalog request without changing MCP tool definitions.
public sealed class GatewayCartridgeLibrary
{
    public const string CatalogPath = "Catalog";
    public const string SelectFunction = "cartridge_select";
    public const string UnloadFunction = "cartridge_unload";
    private const int PageSize = 20;
    private readonly string _directory;
    public string DirectoryPath => _directory;
    public string? DefaultManifest
    {
        get;
    }

    // Throws DirectoryNotFoundException when the configured file or directory does not exist.
    public GatewayCartridgeLibrary(string path)
    {
        string full = Path.GetFullPath(path);
        CartridgeManifest.RejectLinks(full);
        if (File.Exists(full))
        {
            DefaultManifest = full;
            _directory = Path.GetDirectoryName(full)!;
        }
        else if (Directory.Exists(full))
        {
            _directory = full;
        }
        else
        {
            throw new DirectoryNotFoundException("The configured cartridge library does not exist.");
        }
    }

    // Throws ArgumentException for an invalid catalog page and InvalidDataException for an oversized library.
    public JsonObject Browse(string path)
    {
        string[] parts = path.Split('/');
        int offset = 0;
        if (parts.Length > 2 || parts[0] != CatalogPath
            || (parts.Length == 2 && (!int.TryParse(parts[1], out offset) || offset < 0)))
        {
            throw new ArgumentException("Use Catalog or Catalog/<nonnegative offset>.");
        }
        GatewayProgramCartridge[] offers = new GatewayProgramCatalog(_directory).Read().ToArray();
        var page = offers.Skip(offset).Take(PageSize);
        var entries = new JsonArray(page.Select(offer => (JsonNode?)GatewayProgramCatalog.Describe(offer)).ToArray());

        return new JsonObject
        {
            ["success"] = true,
            ["total"] = offers.Length,
            ["next_path"] = offset + PageSize < offers.Length ? CatalogPath + "/" + (offset + PageSize) : null,
            ["cartridges"] = entries,
            ["select_function"] = SelectFunction
        };
    }

    // Throws ArgumentException when the selected key is absent or ambiguous in the configured library.
    public string Resolve(string key)
    {
        var selected = new GatewayProgramCatalog(_directory).Resolve(key);
        if (selected.Native == null)
        {
            throw new ArgumentException("Selected program has no native bundle; use its generated extension route.");
        }

        return selected.Native.Path;
    }

}
