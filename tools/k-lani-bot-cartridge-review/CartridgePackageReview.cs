using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KLaniBot.Actions;

namespace KLaniBot;

// Offline intake validation only. Never executes a recipe, downloads anything,
// accepts an author's approval flag, or authorizes use on another user's node.
public static class CartridgePackageReview
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaximumManifestBytes = 65536;
    private const int MaximumSchemaBytes = 262144;
    private const int MaximumDepth = 12;
    private const int MaximumEntries = 1000;
    private const int MaximumFiles = 200;
    private const int MaximumFileBytes = 1048576;
    private const long MaximumPackageBytes = 5 * 1048576;

    // Throws InvalidDataException naming the first rule of the intake profile the package breaks.
    public static JsonObject Inspect(string directory, string schemaFile)
    {
        string root = Path.GetFullPath(directory);
        RejectLinks(root);
        byte[] manifestBytes = ReadBounded(Path.Combine(root, "manifest.json"), MaximumManifestBytes);
        var manifest = ParseJson(manifestBytes);
        var schema = ReadJson(Path.GetFullPath(schemaFile), MaximumSchemaBytes);
        ToolContract.Validate(schema, manifest, "manifest");
        if (manifest["kind"]!.ToString() != "automation")
            throw new InvalidDataException(
                "This prototype accepts declarative automation intake only; elevated/executable packages need a separate review profile.");

        var package = new PackageFiles(root, DeclaredFiles(manifest));
        package.Walk(root, 0);
        if (package.Actual.Count != package.Expected.Count)
            throw new InvalidDataException("Declared package file is missing.");
        HashSet<string> dependencies = DependencyIds(manifest);
        HashSet<string> requirements = RequiredScopes(manifest, package.Actual);
        return new JsonObject
        {
            ["valid_for_quarantine"] = true,
            ["package_id"] = manifest["id"]!.DeepClone(),
            ["package_version"] = manifest["version"]!.DeepClone(),
            ["package_sha256"] = PackageHash(manifestBytes, package.Actual),
            ["files"] = package.Actual.Count,
            ["bytes"] = package.Size,
            ["required_scopes"] = new JsonArray(requirements
                .Order(StringComparer.Ordinal)
                .Select(s => (JsonNode)JsonValue.Create(s)!)
                .ToArray()),
            ["dependency_resolution"] = dependencies.Count == 0 ? "none" : "not_performed",
            ["signature_verification"] = "not_performed",
            ["malware_assessment"] = "not_performed",
            ["sandbox_replay"] = "not_performed",
            ["release_approved"] = false,
            ["activation_allowed"] = false
        };
    }

    // Throws InvalidDataException for a duplicate, reserved or case-colliding path, a wrong media type or a non-text file.
    private static Dictionary<string, JsonObject> DeclaredFiles(JsonObject manifest)
    {
        var expected = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest["files"]!.AsArray().OfType<JsonObject>())
        {
            string path = item["path"]!.ToString();
            ValidatePath(path);
            if (path == "manifest.json" || !folded.Add(path) || !expected.TryAdd(path, item))
                throw new InvalidDataException("Duplicate, reserved or case-colliding package file.");
            if (item["media_type"]!.ToString() != (Path.GetExtension(path) == ".json" ? "application/json" : "text/markdown"))
                throw new InvalidDataException("File media type differs from its declared text format.");
            if (Path.GetExtension(path) is not (".json" or ".md"))
                throw new InvalidDataException("Scripts, binaries, images and archives are not accepted by this text-only prototype.");
        }
        if (expected.Count == 0)
            throw new InvalidDataException("Package has no declared files.");
        return expected;
    }

    // The files found on disk, each checked against its declaration while the walk counts entries and bytes.
    private sealed class PackageFiles(string root, Dictionary<string, JsonObject> expected)
    {
        private int _count;
        private int _entries;

        public Dictionary<string, JsonObject> Expected => expected;

        public Dictionary<string, byte[]> Actual
        {
            get;
        } = new(StringComparer.Ordinal);

        public long Size
        {
            get;
            private set;
        }

        // Throws InvalidDataException when the package nests too deep or has too many entries.
        public void Walk(string parent, int depth)
        {
            if (depth > MaximumDepth)
                throw new InvalidDataException("Package nesting limit exceeded.");
            foreach (string path in Directory.EnumerateFileSystemEntries(parent))
            {
                if (++_entries > MaximumEntries)
                    throw new InvalidDataException("Package entry limit exceeded.");
                RejectLinks(path);
                if (Directory.Exists(path))
                {
                    Walk(path, depth + 1);
                    continue;
                }
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                ValidatePath(relative);
                if (relative != "manifest.json")
                    Accept(path, relative);
            }
        }

        // Throws InvalidDataException for an undeclared file, a byte limit, a changed or non-text file or a wrong declaration.
        private void Accept(string path, string relative)
        {
            if (++_count > MaximumFiles || !expected.ContainsKey(relative))
                throw new InvalidDataException("Package contains undeclared files or exceeds file limit.");
            long bytes = new FileInfo(path).Length;
            if (bytes < 1 || bytes > MaximumFileBytes || (Size += bytes) > MaximumPackageBytes)
                throw new InvalidDataException("Package byte limit exceeded.");
            var data = ReadBounded(path, MaximumFileBytes);
            if (data.Length != bytes)
                throw new InvalidDataException("Package changed during inspection.");
            Utf8.GetString(data);
            if (data.Contains((byte)0))
                throw new InvalidDataException("NUL in text file.");

            Actual.Add(relative, data);
            var declaration = expected[relative];
            if (declaration["bytes"]!.GetValue<long>() != data.Length || declaration["sha256"]!.ToString() != Hash(data))
                throw new InvalidDataException("Declared file hash or size differs from the actual package.");
            if (relative.EndsWith(".json", StringComparison.Ordinal))
                ParseJson(data);
        }
    }

    // Throws InvalidDataException for a self-dependency or a duplicate dependency id.
    private static HashSet<string> DependencyIds(JsonObject manifest)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dep in manifest["dependencies"]!.AsArray().OfType<JsonObject>())
        {
            string id = dep["id"]!.ToString();
            if (id == manifest["id"]!.ToString() || !ids.Add(id))
                throw new InvalidDataException("Self-dependency or duplicate dependency id.");
        }
        return ids;
    }

    // Every scope a recipe step uses, after checking that the package declared it.
    // Throws InvalidDataException for missing observation, an elevated scope, a duplicate or missing entrypoint, or a step the
    // declarations do not cover.
    private static HashSet<string> RequiredScopes(JsonObject manifest, Dictionary<string, byte[]> actual)
    {
        var permissions = manifest["permissions"]!.AsObject();
        var scopes = permissions["scopes"]!.AsArray().Select(x => x!.ToString()).ToHashSet(StringComparer.Ordinal);
        if (!scopes.Contains("observe"))
            throw new InvalidDataException("Observation capability must be explicit.");
        if (scopes.Contains("admin") || scopes.Contains("shell") || scopes.Contains("process"))
            throw new InvalidDataException("Elevated scopes require a separate profile, not this declarative intake prototype.");
        var effects = permissions["effects"]!.AsArray().Select(x => x!.ToString()).ToHashSet(StringComparer.Ordinal);

        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        var requirements = new HashSet<string>(StringComparer.Ordinal) { "observe" };
        foreach (var entry in manifest["entrypoints"]!.AsArray().OfType<JsonObject>())
        {
            if (!entryNames.Add(entry["name"]!.ToString()))
                throw new InvalidDataException("Duplicate entrypoint.");
            if (!actual.TryGetValue(entry["recipe"]!.ToString(), out var bytes))
                throw new InvalidDataException("Entrypoint recipe is undeclared or missing.");
            var recipe = ParseJson(bytes);
            ToolContract.Validate(RecipeSchema(), recipe, "recipe");
            foreach (var step in recipe["steps"]!.AsArray().OfType<JsonObject>())
                requirements.Add(CheckStep(step, scopes, effects));
        }
        if (entryNames.Count == 0)
            throw new InvalidDataException("Package has no entrypoint.");
        return requirements;
    }

    // Returns the scope the step needs.
    // Throws InvalidDataException for a refused tool, an undeclared scope or effect, or a mutation without a UUIDv7 id.
    private static string CheckStep(JsonObject step, HashSet<string> scopes, HashSet<string> effects)
    {
        string action = step["action"]!.ToString();
        var tool = ToolContract.Get(action);
        if (action is "run_macro" or "save_macro" or "end_session" or "launch_process" or "run_terminal_command")
            throw new InvalidDataException("Dynamic code, process and registry administration are not admitted by this prototype.");
        ToolContract.Validate(tool["inputSchema"]!.AsObject(), step["args"], "step.arguments");
        string scope = tool["scope"]!.ToString();
        if (!scopes.Contains(scope))
            throw new InvalidDataException("A recipe uses an undeclared scope.");
        if (!effects.Contains(Effect(action)))
            throw new InvalidDataException("A recipe uses an undeclared effect.");
        if (tool["mutates"]!.GetValue<bool>()
            && (!Guid.TryParse(step["args"]?["request_id"]?.ToString(), out var id) || id.Version != 7))
            throw new InvalidDataException("Fixture mutations require UUIDv7 request identifiers.");
        return scope;
    }

    // Path and file hash of the manifest and every file in ordinal order, so the same package always hashes the same.
    private static string PackageHash(byte[] manifestBytes, Dictionary<string, byte[]> actual)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        aggregate.AppendData(Encoding.UTF8.GetBytes("manifest.json\0" + Hash(manifestBytes) + "\n"));
        foreach (var pair in actual.OrderBy(p => p.Key, StringComparer.Ordinal))
            aggregate.AppendData(Encoding.UTF8.GetBytes(pair.Key + "\0" + Hash(pair.Value) + "\n"));
        return Convert.ToHexStringLower(aggregate.GetHashAndReset());
    }

    private static JsonObject RecipeSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("format", "steps"),
        ["properties"] = new JsonObject
        {
            ["format"] = new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(1) },
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 100,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("action", "args"),
                    ["properties"] = new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "string", ["maxLength"] = 80 },
                        ["args"] = new JsonObject { ["type"] = "object" }
                    }
                }
            }
        }
    };
    // Throws InvalidDataException when the file exceeds its limit or is no valid JSON object.
    private static JsonObject ReadJson(string file, int limit)
    {
        RejectLinks(file);
        if (new FileInfo(file).Length > limit)
            throw new InvalidDataException("JSON file limit exceeded.");
        return ParseJson(ReadBounded(file, limit));
    }
    // Throws InvalidDataException for duplicate properties or content that is no JSON object.
    private static JsonObject ParseJson(byte[] data)
    {
        Utf8.GetString(data);
        using var doc = JsonDocument.Parse(
            data,
            new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        void Unique(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (!names.Add(p.Name))
                        throw new InvalidDataException("Duplicate JSON property.");
                    Unique(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray())
                    Unique(x);
        }
        Unique(doc.RootElement);
        return JsonNode.Parse(data) as JsonObject ?? throw new InvalidDataException("JSON object required.");
    }
    // Throws InvalidDataException for a tool without an admitted effect.
    private static string Effect(string action) => action switch
    {
        "list_windows" or "get_ui_tree" or "get_chapters" or "inspect_section" or "find_elements" or "get_ui_delta" or "wait_element"
            or "verify_element" or "get_window_geometry" or "take_screenshot" => "read_ui",
        "focus_window" or "close_window" or "set_window_geometry" or "click_element" or "silent_click" or "mouse_click" or "mouse_move"
            or "drag_and_drop" or "hotkey" or "type_text" or "set_text" or "set_clipboard" or "paste_clipboard" => "input_ui",
        "fs_write_file" => "write_workspace",
        "fs_read_file" or "fs_list_directory" or "fs_search_files" or "fs_get_metadata" => "read_workspace",
        _ => throw new InvalidDataException("Tool has no admitted effect mapping in this prototype.")
    };
    // Throws InvalidDataException when the file grows beyond its limit while read.
    private static byte[] ReadBounded(string file, int limit)
    {
        RejectLinks(file);
        using var stream = File.OpenRead(file);
        using var data = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (data.Length + read > limit)
                throw new InvalidDataException("File grew beyond intake limit.");
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    // Throws InvalidDataException for a nonportable, unsafe or reserved path.
    private static void ValidatePath(string path)
    {
        if (!Regex.IsMatch(path, "^[a-z0-9][a-z0-9._/-]{0,179}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("Nonportable package path.");
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part is "" or "." or ".."
                || part.EndsWith('.')
                || stem is "con" or "prn" or "aux" or "nul"
                || Regex.IsMatch(stem, "^(com|lpt)[1-9]$"))
                throw new InvalidDataException("Unsafe or reserved package path.");
        }
    }
    // Throws UnauthorizedAccessException when a part of the path is a link.
    private static void RejectLinks(string path)
    {
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Links are forbidden in cartridge intake.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
