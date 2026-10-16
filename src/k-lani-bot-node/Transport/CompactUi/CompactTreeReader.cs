using System.Text.Json;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// The compact host parses the operated node's tree grammar before issuing any semantic element references.
public static class CompactTreeReader
{
    private static readonly Regex Line = new(
        "^(\\s*)\\[(\\d+)\\] (.+?) (\"(?:[^\"\\\\]|\\\\.)*\") \\(autoId: (\"(?:[^\"\\\\]|\\\\.)*\"), "
        + "val: (null|\"(?:[^\"\\\\]|\\\\.)*\"), enabled: (True|False), offscreen: (True|False)\\)$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string tree, out CompactTreeAttempt<IReadOnlyList<CompactTreeNode>> attempt)
    {
        var nodes = new List<CompactTreeNode>();
        if (tree == null)
        {
            attempt = new(null, CompactTreeError.UnsupportedFormat);
            return false;
        }
        foreach (string row in tree.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            if (!TryNode(row, out CompactTreeNode? node))
            {
                attempt = new(null, CompactTreeError.UnsupportedFormat);
                return false;
            }
            nodes.Add(node!);
        }
        attempt = new(nodes, null);

        return true;
    }

    private static bool TryNode(string row, out CompactTreeNode? node)
    {
        node = null;
        Match match = Line.Match(row);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out int identifier))
        {
            return false;
        }
        try
        {
            string name = JsonSerializer.Deserialize<string>(match.Groups[4].Value)!;
            string autoId = JsonSerializer.Deserialize<string>(match.Groups[5].Value)!;
            string? value = JsonSerializer.Deserialize<string>(match.Groups[6].Value);
            node = new(match.Groups[1].Value.Length, identifier, CompactRole.Parse(match.Groups[3].Value), name, autoId,
                value, match.Groups[7].Value == "True", match.Groups[8].Value == "True");

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
