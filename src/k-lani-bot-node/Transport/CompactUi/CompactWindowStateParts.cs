using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Use case: the bounds of a window reply, shared by the reply code, its regression cases and the JS reference.
public static class CompactWindowStateLimits
{
    public const int DefaultMaxChars = 2000;
    public const int NamespaceLimit = 20;
    public const int RowLimit = 20;
}

// Use case: a window observation asks for a whole window or for one part named by a cursor, with the longest text it may return.
// The cursor has the form PATH@OFFSET, the same path that an earlier reply listed in its more map; null asks for the whole window.
public sealed record CompactWindowStateRequest(
    long WindowHandle,
    CompactWindowKind Kind,
    int MaxChars = CompactWindowStateLimits.DefaultMaxChars,
    string? Cursor = null);

// Use case: the caller turns these reasons into its own answer, so an absent window or an unknown cursor is not an exception.
public enum CompactWindowStateError
{
    NoWindow,
    UnknownPart
}

// Use case: CompactWindowState.Build returns the reply object or the reason it has none, never both.
public sealed record CompactWindowStateAttempt(JsonObject? Value, CompactWindowStateError? Error);

// Use case: CompactWindowState.Build calls this once per request to render the bounded reply of a whole window or of one part.
// Deep namespaces collapse to a count, collections page by rows, long text keeps a start and a fingerprint, and more names each cut.
internal sealed class CompactWindowStateParts
{
    private const int CollapseDepth = 2;
    private const int FingerprintLength = 16;
    private const int FirstDepth = 1;
    private const int NamespaceLimit = CompactWindowStateLimits.NamespaceLimit;
    private const int RowLimit = CompactWindowStateLimits.RowLimit;
    private const string ZeroOffset = "0";
    private static readonly Regex OffsetPattern = new(@"\A[0-9]+\z", RegexOptions.CultureInvariant);
    private readonly CompactWindowStateBuilder _builder;
    private readonly CompactWindowStateRequest _request;
    private readonly string _shape;
    private readonly string? _title;
    private readonly List<string> _lines = new();
    private readonly List<string> _visiblePaths = new();
    private readonly JsonObject _more = new();

    public CompactWindowStateParts(CompactWindowStateBuilder builder, CompactWindowStateRequest request, string shape,
        string? title)
    {
        _builder = builder;
        _request = request;
        _shape = shape;
        _title = title;
    }

    // A null cursor describes the whole window; a cursor describes only the part it names.
    public CompactWindowStateAttempt Page()
    {
        if (_request.Cursor is null)
        {
            return WholeWindow();
        }

        return PageAt(_request.Cursor);
    }

    private CompactWindowStateAttempt WholeWindow()
    {
        _lines.Add(HeaderLine(string.Empty));
        RenderMembers(_builder.Root, FirstDepth, FirstDepth, new List<CompactMemberKey>());
        _lines.Add("}");
        string declaration = string.Join("\n", _lines);

        JsonObject value = new()
        {
            ["window"] = JsonValue.Create(_request.WindowHandle),
            ["kind"] = CompactWindowState.KindName(_request.Kind),
            ["title"] = JsonValue.Create(_title),
            ["declaration"] = declaration,
            ["state"] = PageState()
        };
        AddMore(value);
        value["unmapped"] = CompactWindowState.UnmappedArray(_builder.Unmapped);
        value["hidden_omitted"] = JsonValue.Create(_builder.HiddenOmitted);
        value["shape"] = JsonValue.Create(_shape);
        return new CompactWindowStateAttempt(value, null);
    }

    // The cursor is split at its last '@', so the offset is the text after it and the path is everything before it.
    private CompactWindowStateAttempt PageAt(string cursor)
    {
        int separator = cursor.LastIndexOf('@');
        if (separator < 0)
        {
            return UnknownPart();
        }

        string partPath = cursor.Substring(0, separator);
        string offsetText = cursor.Substring(separator + 1);
        if (!OffsetPattern.IsMatch(offsetText))
        {
            return UnknownPart();
        }

        CompactFoundPart? found = FindPart(_builder.Root, new List<CompactMemberKey>(), partPath);
        if (found is null)
        {
            return UnknownPart();
        }

        if (found.Member.Kind == CompactDeclaredKind.Collection)
        {
            return CollectionPage(partPath, offsetText);
        }

        if (found.Member.Kind == CompactDeclaredKind.Namespace && offsetText == ZeroOffset)
        {
            return NamespacePart(found, partPath);
        }

        return UnknownPart();
    }

    // A namespace part reads as the declaration does at its place: each ancestor opens, the members render, then the ancestors close.
    private CompactWindowStateAttempt NamespacePart(CompactFoundPart found, string partPath)
    {
        IReadOnlyList<CompactMemberKey> path = found.Path;
        _lines.Add(HeaderLine(", part " + partPath));
        for (int depth = 0; depth < path.Count; depth++)
        {
            AddLine(depth + 1, CompactWindowStateNames.RenderKey(path[depth]) + ": {", string.Empty);
        }

        RenderMembers(found.Member.Children!, path.Count + 1, FirstDepth, path);
        for (int depth = path.Count; depth >= FirstDepth; depth--)
        {
            AddLine(depth, "};", string.Empty);
        }

        _lines.Add("}");
        string declaration = string.Join("\n", _lines);

        JsonObject value = new()
        {
            ["window"] = JsonValue.Create(_request.WindowHandle),
            ["kind"] = CompactWindowState.KindName(_request.Kind),
            ["part"] = partPath,
            ["declaration"] = declaration,
            ["state"] = PageState()
        };
        AddMore(value);
        value["shape"] = JsonValue.Create(_shape);
        return new CompactWindowStateAttempt(value, null);
    }

    // A page holds RowLimit rows from its offset; a page that stops short names the next cursor in more.
    private CompactWindowStateAttempt CollectionPage(string partPath, string offsetText)
    {
        JsonArray rows = CollectionRows(partPath);
        long offset = ParseOffset(offsetText);
        int start = (int)Math.Min(offset, rows.Count);
        int end = Math.Min(start + RowLimit, rows.Count);
        if (offset < rows.Count - RowLimit)
        {
            _more[partPath] = JsonValue.Create(FormattableString.Invariant($"{partPath}@{offset + RowLimit}"));
        }

        JsonObject value = new()
        {
            ["window"] = JsonValue.Create(_request.WindowHandle),
            ["kind"] = CompactWindowState.KindName(_request.Kind),
            ["part"] = partPath,
            ["rows"] = ClipRows(rows, start, end),
            ["total"] = JsonValue.Create(rows.Count)
        };
        AddMore(value);
        value["shape"] = JsonValue.Create(_shape);
        return new CompactWindowStateAttempt(value, null);
    }

    private static CompactWindowStateAttempt UnknownPart()
    {
        return new CompactWindowStateAttempt(null, CompactWindowStateError.UnknownPart);
    }

    // An offset beyond the long range lies past every row, so it reads as the largest offset.
    private static long ParseOffset(string offsetText)
    {
        bool parsed = long.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out long offset);
        return parsed ? offset : long.MaxValue;
    }

    // Members are searched depth first in declaration order, so the first member whose path equals the cursor part wins.
    private static CompactFoundPart? FindPart(CompactNamespace ns, IReadOnlyList<CompactMemberKey> parentPath, string target)
    {
        foreach (CompactDeclaredMember member in ns.Members)
        {
            List<CompactMemberKey> memberPath = new(parentPath) { member.Key };
            if (CompactWindowStateNames.JoinPath(parentPath, member.Key) == target)
            {
                return new CompactFoundPart(member, memberPath);
            }

            if (member.Kind == CompactDeclaredKind.Namespace)
            {
                CompactFoundPart? nested = FindPart(member.Children!, memberPath, target);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    // Members render in declaration order; a namespace renders its own members one level deeper.
    private void RenderMembers(CompactNamespace ns, int level, int relativeDepth, IReadOnlyList<CompactMemberKey> path)
    {
        foreach (CompactDeclaredMember member in ns.Members)
        {
            RenderMember(member, level, relativeDepth, path);
        }
    }

    private void RenderMember(CompactDeclaredMember member, int level, int relativeDepth, IReadOnlyList<CompactMemberKey> path)
    {
        switch (member.Kind)
        {
            case CompactDeclaredKind.Property:
                RenderProperty(member, level, path);
                break;
            case CompactDeclaredKind.Collection:
                RenderCollection(member, level, path);
                break;
            case CompactDeclaredKind.Method:
                AddLine(level, NameOf(member) + "(): void;", CompactWindowState.NotesTail(member.Notes));
                break;
            case CompactDeclaredKind.Namespace:
                RenderNamespace(member, level, relativeDepth, path);
                break;
        }
    }

    private void RenderProperty(CompactDeclaredMember member, int level, IReadOnlyList<CompactMemberKey> path)
    {
        string typeText = CompactWindowState.TypeText(member.ValueType);
        AddLine(level, NameOf(member) + ": " + typeText + ";", CompactWindowState.NotesTail(member.Notes));
        _visiblePaths.Add(StatePathOf(member, path));
    }

    // A collection past the row limit names its size in its notes and points its more entry at the next page.
    private void RenderCollection(CompactDeclaredMember member, int level, IReadOnlyList<CompactMemberKey> path)
    {
        string statePath = StatePathOf(member, path);
        JsonArray rows = CollectionRows(statePath);
        string tail = CompactWindowState.NotesTail(member.Notes);
        if (rows.Count > RowLimit)
        {
            tail = AppendNote(tail, FormattableString.Invariant($"{rows.Count} rows"));
            _more[statePath] = JsonValue.Create(FormattableString.Invariant($"{statePath}@{RowLimit}"));
        }

        AddLine(level, "readonly " + NameOf(member) + ": string[];", tail);
        _visiblePaths.Add(statePath);
    }

    // A namespace two levels below the rendered part collapses when it is large, so its members and state stay out of the reply.
    private void RenderNamespace(CompactDeclaredMember member, int level, int relativeDepth, IReadOnlyList<CompactMemberKey> path)
    {
        string statePath = StatePathOf(member, path);
        string tail = CompactWindowState.NotesTail(member.Notes);
        int leafCount = LeafCount(member.Children!);
        if (relativeDepth >= CollapseDepth && leafCount > NamespaceLimit)
        {
            AddLine(level, FormattableString.Invariant($"{NameOf(member)}: {{ /* {leafCount} members */ }};"), tail);
            _more[statePath] = JsonValue.Create(statePath + "@" + ZeroOffset);
            return;
        }

        AddLine(level, NameOf(member) + ": {", tail);
        List<CompactMemberKey> childPath = new(path) { member.Key };
        RenderMembers(member.Children!, level + 1, relativeDepth + 1, childPath);
        AddLine(level, "};", string.Empty);
    }

    // Every declared member below a namespace counts once, including the members of namespaces nested in it.
    private static int LeafCount(CompactNamespace ns)
    {
        int total = 0;
        foreach (CompactDeclaredMember member in ns.Members)
        {
            total += member.Kind == CompactDeclaredKind.Namespace ? LeafCount(member.Children!) : 1;
        }

        return total;
    }

    // The note joins the notes that the member already has, or starts the comment when it has none.
    private static string AppendNote(string tail, string note)
    {
        if (tail.Length == 0)
        {
            return " // " + note;
        }

        return tail + ", " + note;
    }

    // Rendered properties and collections list their state in render order; a secret has no state entry and is skipped,
    // and a collapsed namespace never reaches this list.
    private JsonObject PageState()
    {
        JsonObject page = new();
        foreach (string statePath in _visiblePaths)
        {
            if (_builder.State.ContainsKey(statePath))
            {
                page[statePath] = ClipNode(_builder.State[statePath]);
            }
        }

        return page;
    }

    // Values are copied, so the builder keeps its own nodes; text and rows are clipped, other values are copied as they are.
    private JsonNode? ClipNode(JsonNode? value)
    {
        if (value is JsonArray rows)
        {
            return ClipRows(rows, 0, Math.Min(rows.Count, RowLimit));
        }

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
        {
            return ClipText(text, _request.MaxChars);
        }

        return value?.DeepClone();
    }

    private JsonArray ClipRows(JsonArray rows, int start, int end)
    {
        JsonArray page = new();
        for (int index = start; index < end; index++)
        {
            page.Add(ClipText(rows[index]!.GetValue<string>(), _request.MaxChars));
        }

        return page;
    }

    // Text longer than the limit keeps its first characters, its length in code points and a fingerprint of the whole text.
    internal static JsonNode ClipText(string text, int maxChars)
    {
        int length = CountRunes(text);
        if (length <= maxChars)
        {
            return JsonValue.Create(text)!;
        }

        JsonObject clipped = new();
        clipped["start"] = JsonValue.Create(FirstRunes(text, maxChars));
        clipped["length"] = JsonValue.Create(length);
        clipped["sha256"] = JsonValue.Create(Fingerprint(text));
        return clipped;
    }

    private static int CountRunes(string text)
    {
        return text.EnumerateRunes().Count();
    }

    // Cutting by code points keeps a surrogate pair whole, so the start is always valid text.
    private static string FirstRunes(string text, int count)
    {
        StringBuilder prefix = new();
        foreach (Rune rune in text.EnumerateRunes().Take(count))
        {
            prefix.Append(rune.ToString());
        }

        return prefix.ToString();
    }

    // The fingerprint is the start of the SHA-256 digest of the UTF-8 text, written in lower case hexadecimal.
    internal static string Fingerprint(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(digest).ToLowerInvariant().Substring(0, FingerprintLength);
    }

    // Every collection has its rows in the state, so a missing array means the builder is broken, not the input.
    private JsonArray CollectionRows(string statePath)
    {
        if (_builder.State[statePath] is not JsonArray rows)
        {
            throw new UnreachableException();
        }

        return rows;
    }

    private void AddMore(JsonObject value)
    {
        if (_more.Count > 0)
        {
            value["more"] = _more;
        }
    }

    private void AddLine(int level, string text, string tail)
    {
        _lines.Add(CompactWindowState.IndentFor(level) + text + tail);
    }

    private string HeaderLine(string partSuffix)
    {
        string className = CompactWindowState.ClassName(_request.Kind);
        string kindName = CompactWindowState.KindName(_request.Kind);
        return FormattableString.Invariant($"class {className} {{ // window {_request.WindowHandle}, kind {kindName}") + partSuffix;
    }

    private static string NameOf(CompactDeclaredMember member)
    {
        return CompactWindowStateNames.RenderKey(member.Key);
    }

    private static string StatePathOf(CompactDeclaredMember member, IReadOnlyList<CompactMemberKey> path)
    {
        return CompactWindowStateNames.JoinPath(path, member.Key);
    }
}

// Use case: CompactWindowStateParts answers a cursor with the member that it names and the full path to that member.
internal sealed record CompactFoundPart(CompactDeclaredMember Member, IReadOnlyList<CompactMemberKey> Path);
