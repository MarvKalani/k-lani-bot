using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Platforms;

namespace KLaniBot.Actions;

// Use case: before the agent acts on a dialog, or on a control whose rule requires a decision, the journal records its
// reasons. This adds what the node itself saw: the program and its hash, the window, the element and the dialog's text,
// so a later review can check what the agent was answering. Field values are never part of the text.
internal static class DialogEvidence
{
    public const int ExcerptCharacters = 200;

    // Hashing a program reads all of it, so a hash is kept while the file's size and time stay the same.
    private static readonly Dictionary<string, string> ProgramHashes = new();

    // The element the request names, or null when it names none or the backend cannot identify it.
    public static UiTargetIdentity? TargetOf(IUiService ui, JsonObject arguments)
    {
        int? elementId = arguments["id"]?.GetValue<int>();
        return elementId == null ? null : IdentityOrNull(ui, elementId.Value);
    }

    public static JsonObject Describe(IUiService ui, JsonObject arguments, DialogTextEvidence mode)
    {
        int? elementId = arguments["id"]?.GetValue<int>();
        UiTargetIdentity? target = TargetOf(ui, arguments);
        if (elementId == null || target == null)
        {
            return new JsonObject { ["available"] = false };
        }

        var evidence = new JsonObject
        {
            ["available"] = true,
            ["program"] = target.ExecutablePath,
            ["program_sha256"] = ProgramHash(target.ExecutablePath),
            ["window_title"] = target.WindowTitle,
            ["role"] = target.Role,
            ["name"] = target.Name
        };
        AddText(evidence, ui.ReadWindowText(elementId.Value), mode);
        return evidence;
    }

    // The advisor reads the dialog's text to classify it; the journal keeps only what the administrator chose to keep.
    public static JsonObject ForJournal(JsonObject evidence, DialogTextEvidence mode)
    {
        var kept = (JsonObject)evidence.DeepClone();
        switch (mode)
        {
            case DialogTextEvidence.Excerpt:
                break;
            case DialogTextEvidence.HashOnly:
                kept.Remove("text_excerpt");
                break;
            default:
                throw new UnreachableException();
        }
        return kept;
    }

    // Evidence never fails the action: an element the backend cannot identify is recorded as unavailable.
    private static UiTargetIdentity? IdentityOrNull(IUiService ui, int elementId)
    {
        try
        {
            return ui.GetTargetIdentity(elementId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddText(JsonObject evidence, string? text, DialogTextEvidence mode)
    {
        if (text == null)
        {
            evidence["text_available"] = false;
            return;
        }

        evidence["text_available"] = true;
        evidence["text_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        switch (mode)
        {
            case DialogTextEvidence.Excerpt:
                evidence["text_excerpt"] = Excerpt(text);
                break;
            case DialogTextEvidence.HashOnly:
                break;
            default:
                throw new UnreachableException();
        }
    }

    // Whole characters as the text shows them, never half of a surrogate pair.
    private static string Excerpt(string text)
    {
        var excerpt = new StringBuilder();
        var characters = StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        while (count < ExcerptCharacters && characters.MoveNext())
        {
            excerpt.Append(characters.GetTextElement());
            count++;
        }
        return excerpt.ToString();
    }

    // Null when the program cannot be read; the evidence then says so instead of failing the action.
    private static string? ProgramHash(string path)
    {
        try
        {
            var file = new FileInfo(path);
            string key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            lock (ProgramHashes)
            {
                if (ProgramHashes.TryGetValue(key, out string? known))
                {
                    return known;
                }
            }
            using var stream = file.OpenRead();
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            lock (ProgramHashes)
            {
                ProgramHashes[key] = hash;
            }
            return hash;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
