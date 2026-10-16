using System.Text.Json;

namespace RepositoryGuard;

// A line over the limit: file, line number and length.
public sealed record OverlongLine(string Path, int Line, int Length);

// AGENTS.md asks for one statement per line and code that is easy to read in a debugger. A fixed limit is the
// one part of that rule that needs no judgement, so a long line cannot come back unnoticed after the cleanup.
// The check reads the file system, not Git, so it can run on the working tree and on checked-out snapshots alike.
public static class LineLengthPolicy
{
    public const int MaximumLength = 140;

    private static readonly string[] SourceRoots = ["src", "tests", "tools"];
    private static readonly string[] GeneratedDirectories = ["obj", "bin"];

    // Characters are counted, not bytes, so an umlaut costs the same as any other letter.
    public static IReadOnlyList<OverlongLine> Inspect(string path, string text)
    {
        var findings = new List<OverlongLine>();
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (line.Length > MaximumLength)
                findings.Add(new OverlongLine(path, index + 1, line.Length));
        }
        return findings;
    }

    public static IReadOnlyList<OverlongLine> InspectTree(string root, out int filesScanned)
    {
        var findings = new List<OverlongLine>();
        filesScanned = 0;
        foreach (string sourceRoot in SourceRoots)
        {
            string directory = Path.Combine(root, sourceRoot);
            if (!Directory.Exists(directory))
                continue;
            var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
            foreach (string file in files)
            {
                string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsGenerated(relativePath))
                    continue;
                filesScanned++;
                findings.AddRange(Inspect(relativePath, File.ReadAllText(file)));
            }
        }
        return findings;
    }

    public static int Run(string root)
    {
        var findings = InspectTree(root, out int filesScanned);
        foreach (var finding in findings)
            Console.Error.WriteLine($"{finding.Path}:{finding.Line}: {finding.Length} characters, limit {MaximumLength}");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = findings.Count == 0,
            mode = "line-length",
            limit = MaximumLength,
            files_scanned = filesScanned,
            findings
        }));
        return findings.Count == 0 ? 0 : 1;
    }

    private static bool IsGenerated(string relativePath)
    {
        string[] parts = relativePath.Split('/');
        return parts.Any(part => GeneratedDirectories.Contains(part, StringComparer.Ordinal));
    }
}
