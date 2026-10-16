using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Actions;

// Store state and transition fingerprints, never raw labels, field values or screenshots.
// Selectors still require a fresh unique observation; graph similarity never authorizes a click.
public sealed class UiStateAtlas(string stateRoot)
{
    private string? _previous;
    public string Observe(string platform, string tree)
    {
        string normalized = Regex.Replace(tree, @"(?m)^(\s*(?:- )?)\[\d+\]", "$1[ref]");
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(platform + "\n" + normalized)));
        var edge = new JsonObject
        {
            ["version"] = 1,
            ["platform"] = platform,
            ["from"] = _previous,
            ["to"] = fingerprint,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
        File.AppendAllText(Path.Combine(stateRoot, "ui-states.jsonl"), edge.ToJsonString() + "\n");
        _previous = fingerprint;
        return fingerprint;
    }
}
