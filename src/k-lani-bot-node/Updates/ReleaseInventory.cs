using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLaniBot.Startup;

namespace KLaniBot.Updates;

// The release command lists every file of a package with its size and hash, so the package can be checked against exactly this list.
public static class ReleaseInventory
{
    // Throws InvalidDataException when a file is a link.
    public static JsonObject Create(string directory)
    {
        var files = new JsonArray();
        var allFiles = Directory.EnumerateFiles(Path.GetFullPath(directory), "*", SearchOption.AllDirectories);
        foreach (string file in allFiles.Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Release inventory does not follow links.");
            using var stream = File.OpenRead(file);
            files.Add(
                new JsonObject
                {
                    ["path"] = Path.GetRelativePath(directory, file),
                    ["bytes"] = stream.Length,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream))
                });
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["product"] = "k-lani-bot",
            ["assembly_version"] = typeof(Program).Assembly.GetName().Version?.ToString(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["dependencies"] = new JsonArray(typeof(Program).Assembly.GetReferencedAssemblies()
                .OrderBy(a => a.Name)
                .Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["version"] = a.Version?.ToString() })
                .ToArray()),
            ["files"] = files
        };
    }
}
