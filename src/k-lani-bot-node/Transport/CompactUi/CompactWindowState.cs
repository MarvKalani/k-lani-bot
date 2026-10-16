using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Use case: the window observation calls Build for one window's native tree. The declaration lists the window's members,
// the state holds their current values, and the shape names the declaration so that a changed window gets a new shape.
public static class CompactWindowState
{
    private const string IndentUnit = "  ";
    private const int ShapeLength = 16;
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex WindowRolePattern = new(@"\A(?:window|frame|dialog|file chooser)\z", MatchOptions);
    private static readonly Regex NotesPattern = new(" // .*$", RegexOptions.CultureInvariant);

    // A tree without a window and a cursor that names no part are typed results, so neither case throws.
    // The shape names the whole window's declaration, so a bounded reply still carries the same shape.
    public static CompactWindowStateAttempt Build(IReadOnlyList<CompactTreeNode> nodes, CompactWindowStateRequest request)
    {
        CompactWindowStateBuilder? builder = CollectBuilder(nodes);
        if (builder is null)
        {
            return new CompactWindowStateAttempt(null, CompactWindowStateError.NoWindow);
        }

        CompactWindowStateParts parts = new(builder, request, ShapeOf(builder, request), WindowTitle(nodes));
        return parts.Page();
    }

    // Bind walks the same tree as Build, so the paths it names are the paths that a reply declares.
    public static CompactWindowBindingsAttempt Bind(IReadOnlyList<CompactTreeNode> nodes)
    {
        CompactWindowStateBuilder? builder = CollectBuilder(nodes);
        if (builder is null)
        {
            return new CompactWindowBindingsAttempt(null, CompactWindowStateError.NoWindow);
        }

        return new CompactWindowBindingsAttempt(builder.Bindings, null);
    }

    // Wire names are the lower case words that the bindings and the window diff reply carry.
    public static string WireName(CompactBoundMember member)
    {
        return member switch
        {
            CompactBoundMember.Text => "text",
            CompactBoundMember.Secret => "secret",
            CompactBoundMember.Number => "number",
            CompactBoundMember.Choice => "choice",
            CompactBoundMember.Collection => "collection",
            CompactBoundMember.Namespace => "namespace",
            CompactBoundMember.Method => "method",
            CompactBoundMember.Toggle => "toggle",
            _ => throw new UnreachableException()
        };
    }

    // A tree without a window has no builder; the callers turn that absence into the NoWindow reason.
    internal static CompactWindowStateBuilder? CollectBuilder(IReadOnlyList<CompactTreeNode> nodes)
    {
        int? windowIndex = FindWindowIndex(nodes);
        if (windowIndex is null)
        {
            return null;
        }

        CompactWindowStateBuilder builder = new(WindowBody(nodes, windowIndex.Value));
        builder.CollectMembers();
        return builder;
    }

    // The window diff compares shapes of the same declaration that a reply names, so both sides agree on the name.
    internal static string ShapeOf(CompactWindowStateBuilder builder, CompactWindowStateRequest request)
    {
        string declaration = RenderDeclaration(request.WindowHandle, request.Kind, builder.Root);
        return Shape(declaration);
    }

    // The window row's name is the caption the user reads; the diff reports it as evidence and never as identity.
    internal static string? WindowTitle(IReadOnlyList<CompactTreeNode> nodes)
    {
        int? windowIndex = FindWindowIndex(nodes);
        if (windowIndex is null)
        {
            return null;
        }

        return nodes[windowIndex.Value].Name;
    }

    private static List<CompactTreeNode> WindowBody(IReadOnlyList<CompactTreeNode> nodes, int windowIndex)
    {
        int windowDepth = nodes[windowIndex].Depth;
        List<CompactTreeNode> body = new();
        for (int index = windowIndex + 1; index < nodes.Count && nodes[index].Depth > windowDepth; index++)
        {
            body.Add(nodes[index]);
        }

        return body;
    }

    // The first window, frame or dialog row names the window; null means that the tree has none.
    private static int? FindWindowIndex(IReadOnlyList<CompactTreeNode> nodes)
    {
        for (int index = 0; index < nodes.Count; index++)
        {
            if (WindowRolePattern.IsMatch(nodes[index].Role.Name))
            {
                return index;
            }
        }

        return null;
    }

    private static string RenderDeclaration(long windowHandle, CompactWindowKind kind, CompactNamespace root)
    {
        List<string> lines = new();
        lines.Add(FormattableString.Invariant($"class {ClassName(kind)} {{ // window {windowHandle}, kind {KindName(kind)}"));
        RenderNamespace(root, 1, lines);
        lines.Add("}");
        return string.Join("\n", lines);
    }

    // Members render in the order they were declared; a namespace renders its own members one indentation level deeper.
    private static void RenderNamespace(CompactNamespace ns, int level, List<string> lines)
    {
        string pad = IndentFor(level);
        foreach (CompactDeclaredMember member in ns.Members)
        {
            RenderMember(member, pad, level, lines);
        }
    }

    private static void RenderMember(CompactDeclaredMember member, string pad, int level, List<string> lines)
    {
        string name = CompactWindowStateNames.RenderKey(member.Key);
        string tail = NotesTail(member.Notes);
        switch (member.Kind)
        {
            case CompactDeclaredKind.Property:
                lines.Add(pad + name + ": " + TypeText(member.ValueType) + ";" + tail);
                break;
            case CompactDeclaredKind.Collection:
                lines.Add(pad + "readonly " + name + ": string[];" + tail);
                break;
            case CompactDeclaredKind.Method:
                lines.Add(pad + name + "(): void;" + tail);
                break;
            case CompactDeclaredKind.Namespace:
                lines.Add(pad + name + ": {" + tail);
                RenderNamespace(member.Children!, level + 1, lines);
                lines.Add(pad + "};");
                break;
        }
    }

    internal static string IndentFor(int level)
    {
        return string.Concat(Enumerable.Repeat(IndentUnit, level));
    }

    internal static string TypeText(CompactValueType valueType)
    {
        return valueType switch
        {
            CompactValueType.Text => "string",
            CompactValueType.Number => "number",
            _ => throw new UnreachableException()
        };
    }

    internal static string NotesTail(IReadOnlyList<CompactMemberNote> notes)
    {
        if (notes.Count == 0)
        {
            return string.Empty;
        }

        List<string> noteTexts = new();
        foreach (CompactMemberNote note in notes)
        {
            noteTexts.Add(NoteText(note));
        }

        return " // " + string.Join(", ", noteTexts);
    }

    private static string NoteText(CompactMemberNote note)
    {
        return note switch
        {
            CompactMemberNote.Disabled => "disabled",
            CompactMemberNote.WriteOnly => "write-only",
            CompactMemberNote.Choice => "choice",
            CompactMemberNote.Toggle => "toggle",
            _ => throw new UnreachableException()
        };
    }

    internal static string ClassName(CompactWindowKind kind)
    {
        return kind switch
        {
            CompactWindowKind.Main => "Main",
            CompactWindowKind.Dialog => "Dialog",
            _ => throw new UnreachableException()
        };
    }

    internal static string KindName(CompactWindowKind kind)
    {
        return kind switch
        {
            CompactWindowKind.Main => "main",
            CompactWindowKind.Dialog => "dialog",
            _ => throw new UnreachableException()
        };
    }

    internal static JsonArray UnmappedArray(IEnumerable<CompactUnmappedEntry> entries)
    {
        JsonArray array = new();
        foreach (CompactUnmappedEntry entry in entries)
        {
            array.Add(new JsonObject
            {
                ["role"] = entry.Role,
                ["name"] = entry.Name,
                ["count"] = JsonValue.Create(entry.Count)
            });
        }

        return array;
    }

    // Lines after the header lose their notes, so enabling a control changes the state and not the shape of the window.
    private static string ShapeText(string declaration)
    {
        string[] lines = declaration.Split('\n');
        List<string> shapeLines = new() { lines[0] };
        for (int index = 1; index < lines.Length; index++)
        {
            shapeLines.Add(NotesPattern.Replace(lines[index], string.Empty));
        }

        return string.Join("\n", shapeLines);
    }

    // The shape hashes the declaration without its notes, so current values and secret values never change it.
    private static string Shape(string declaration)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(ShapeText(declaration)));
        return Convert.ToHexString(digest).ToLowerInvariant().Substring(0, ShapeLength);
    }
}

// Use case: Build creates one builder per window. It walks the window body once and keeps the members, the current values,
// the unmapped rows and the count of hidden nodes, from which the declaration and the state object are rendered.
internal sealed class CompactWindowStateBuilder
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex CollectionRowPattern = new("item|cell|row", MatchOptions);
    private static readonly Regex LabelRolePattern = new(@"\A(?:label|static|statictext|text)\z", MatchOptions);
    private readonly IReadOnlyList<CompactTreeNode> _body;
    private readonly List<string> _labels = new();
    private readonly List<CompactUnmappedEntry> _unmapped = new();
    private int _hiddenOmitted;
    private readonly OrderedDictionary<string, CompactMemberBinding> _bindings = new();

    public CompactWindowStateBuilder(IReadOnlyList<CompactTreeNode> body)
    {
        _body = body;
    }

    public CompactNamespace Root { get; } = new();

    public JsonObject State { get; } = new();

    public IReadOnlyDictionary<string, CompactMemberBinding> Bindings => _bindings;

    public IReadOnlyList<CompactUnmappedEntry> Unmapped => _unmapped;

    public int HiddenOmitted => _hiddenOmitted;

    // Labels are collected after the walk, so the root collection of texts follows every member of the window.
    public void CollectMembers()
    {
        WalkRange(0, _body.Count, Root, new List<CompactMemberKey>(), false);
        AddLabelsCollection();
    }

    private void WalkRange(int start, int end, CompactNamespace ns, IReadOnlyList<CompactMemberKey> path, bool inNamespace)
    {
        int index = start;
        while (index < end)
        {
            index = VisitNode(index, ns, path, inNamespace);
        }
    }

    // Every branch consumes the node and its descendants, so the returned index is the first node after them.
    private int VisitNode(int index, CompactNamespace ns, IReadOnlyList<CompactMemberKey> path, bool inNamespace)
    {
        CompactTreeNode node = _body[index];
        int stop = ChildrenEnd(index);
        if (node.Offscreen && !inNamespace)
        {
            _hiddenOmitted += stop - index;
            return stop;
        }

        bool hasChildren = stop > index + 1;
        CompactMemberClass memberClass = CompactWindowClassifier.Classify(node.Role.Name, node.Name, node.Value, hasChildren);
        VisitClassified(memberClass, index, stop, ns, path, inNamespace);
        return stop;
    }

    private void VisitClassified(
        CompactMemberClass memberClass,
        int index,
        int stop,
        CompactNamespace ns,
        IReadOnlyList<CompactMemberKey> path,
        bool inNamespace)
    {
        CompactTreeNode node = _body[index];
        switch (memberClass)
        {
            case CompactMemberClass.Text:
            case CompactMemberClass.Secret:
            case CompactMemberClass.Number:
            case CompactMemberClass.Choice:
                AddProperty(node, memberClass, ns, path);
                break;
            case CompactMemberClass.Collection:
                AddCollection(node, index, stop, ns, path);
                break;
            case CompactMemberClass.Namespace:
                AddNamespace(node, index, stop, ns, path);
                break;
            case CompactMemberClass.Container:
                WalkRange(index + 1, stop, ns, path, inNamespace);
                break;
            case CompactMemberClass.Method:
            case CompactMemberClass.Toggle:
                AddMethod(node, memberClass, ns, path, index, stop);
                break;
            case CompactMemberClass.Label:
                AddLabel(node);
                break;
            case CompactMemberClass.Ignore:
                break;
            case CompactMemberClass.Unmapped:
                CountUnmapped(node.Role.Name, node.Name.Trim());
                break;
        }
    }

    private void AddProperty(
        CompactTreeNode node,
        CompactMemberClass memberClass,
        CompactNamespace ns,
        IReadOnlyList<CompactMemberKey> path)
    {
        List<CompactMemberNote> notes = DisabledNotes(node);
        if (memberClass == CompactMemberClass.Secret)
        {
            notes.Add(CompactMemberNote.WriteOnly);
        }

        if (memberClass == CompactMemberClass.Choice)
        {
            notes.Add(CompactMemberNote.Choice);
        }

        string text = CompactWindowStateNames.SourceText(node.Name);
        CompactMemberKey key = ns.AddKey(text, PropertyFallback(memberClass));
        CompactValueType valueType = memberClass == CompactMemberClass.Number ? CompactValueType.Number : CompactValueType.Text;
        ns.Members.Add(new CompactDeclaredMember(CompactDeclaredKind.Property, key, notes, valueType, null));
        string statePath = CompactWindowStateNames.JoinPath(path, key);
        RecordBinding(statePath, node, PropertyBoundMember(memberClass), text.Length == 0);
        AddPropertyState(node, memberClass, statePath);
    }

    private static CompactBoundMember PropertyBoundMember(CompactMemberClass memberClass)
    {
        if (memberClass == CompactMemberClass.Secret)
        {
            return CompactBoundMember.Secret;
        }

        if (memberClass == CompactMemberClass.Number)
        {
            return CompactBoundMember.Number;
        }

        if (memberClass == CompactMemberClass.Choice)
        {
            return CompactBoundMember.Choice;
        }

        return CompactBoundMember.Text;
    }

    // Secret values never enter the state, so a password cannot be read back from a window reply.
    private void AddPropertyState(CompactTreeNode node, CompactMemberClass memberClass, string statePath)
    {
        if (memberClass == CompactMemberClass.Secret)
        {
            return;
        }

        if (memberClass == CompactMemberClass.Number && CompactWindowStateNames.TryNumber(node.Value, out JsonValue? number))
        {
            State[statePath] = number;
            return;
        }

        State[statePath] = node.Value is null ? null : JsonValue.Create(node.Value);
    }

    private void AddCollection(CompactTreeNode node, int index, int stop, CompactNamespace ns, IReadOnlyList<CompactMemberKey> path)
    {
        List<CompactMemberNote> notes = DisabledNotes(node);
        string text = CompactWindowStateNames.SourceText(node.Name);
        CompactMemberKey key = ns.AddKey(text, "items");
        ns.Members.Add(new CompactDeclaredMember(CompactDeclaredKind.Collection, key, notes, CompactValueType.Text, null));
        string statePath = CompactWindowStateNames.JoinPath(path, key);
        RecordBinding(statePath, node, CompactBoundMember.Collection, text.Length == 0);
        State[statePath] = ToJsonArray(CollectRows(index + 1, stop));
    }

    // A namespace whose members are all unmapped contributes no members and releases its key again.
    private void AddNamespace(CompactTreeNode node, int index, int stop, CompactNamespace ns, IReadOnlyList<CompactMemberKey> path)
    {
        string text = CompactWindowStateNames.SourceText(node.Name);
        if (text.Length == 0)
        {
            CountUnmapped(node.Role.Name, node.Name.Trim());
            return;
        }

        List<CompactMemberNote> notes = DisabledNotes(node);
        CompactMemberKey key = ns.AddKey(text, "menu");
        string namespacePath = CompactWindowStateNames.JoinPath(path, key);
        RecordBinding(namespacePath, node, CompactBoundMember.Namespace, false);
        CompactNamespace child = new();
        WalkRange(index + 1, stop, child, ExtendPath(path, key), true);
        if (child.Members.Count == 0)
        {
            ns.ReleaseKey(key);
            _bindings.Remove(namespacePath);
            return;
        }

        ns.Members.Add(new CompactDeclaredMember(CompactDeclaredKind.Namespace, key, notes, CompactValueType.Text, child));
    }

    private void AddMethod(
        CompactTreeNode node,
        CompactMemberClass memberClass,
        CompactNamespace ns,
        IReadOnlyList<CompactMemberKey> path,
        int index,
        int stop)
    {
        string text = CompactWindowStateNames.SourceText(node.Name);
        if (text.Length == 0)
        {
            text = CaptionFromDescendants(index, stop);
        }

        if (text.Length == 0)
        {
            CountUnmapped(node.Role.Name, string.Empty);
            return;
        }

        List<CompactMemberNote> notes = DisabledNotes(node);
        if (memberClass == CompactMemberClass.Toggle)
        {
            notes.Add(CompactMemberNote.Toggle);
        }

        // The text is never empty here, so the fallback key is not used.
        CompactMemberKey key = ns.AddKey(text, text);
        ns.Members.Add(new CompactDeclaredMember(CompactDeclaredKind.Method, key, notes, CompactValueType.Text, null));
        CompactBoundMember boundMember = memberClass == CompactMemberClass.Toggle ? CompactBoundMember.Toggle : CompactBoundMember.Method;
        RecordBinding(CompactWindowStateNames.JoinPath(path, key), node, boundMember, false);
    }

    private void AddLabel(CompactTreeNode node)
    {
        string valueText = (node.Value ?? string.Empty).Trim();
        string label = valueText.Length > 0 ? valueText : node.Name.Trim();
        if (label.Length > 0)
        {
            _labels.Add(label);
        }
    }

    private void AddLabelsCollection()
    {
        if (_labels.Count == 0)
        {
            return;
        }

        CompactMemberKey key = Root.AddKey("texts", "texts");
        List<CompactMemberNote> notes = new();
        Root.Members.Add(new CompactDeclaredMember(CompactDeclaredKind.Collection, key, notes, CompactValueType.Text, null));
        State[key.Name] = ToJsonArray(_labels);
    }

    // Rows are the item, cell and row descendants of a collection, so a list without names still reports its entries.
    private List<string> CollectRows(int start, int end)
    {
        List<string> rows = new();
        for (int index = start; index < end; index++)
        {
            CompactTreeNode row = _body[index];
            if (CollectionRowPattern.IsMatch(row.Role.Name))
            {
                rows.Add(RowText(row));
            }
        }

        return rows;
    }

    private static string RowText(CompactTreeNode row)
    {
        string name = row.Name.Trim();
        return name.Length > 0 ? name : (row.Value ?? string.Empty);
    }

    // A button without its own name is named by its first labelled descendant, the caption a user reads on it.
    private string CaptionFromDescendants(int index, int stop)
    {
        for (int row = index + 1; row < stop; row++)
        {
            CompactTreeNode candidate = _body[row];
            if (!LabelRolePattern.IsMatch(candidate.Role.Name))
            {
                continue;
            }

            string caption = CompactWindowStateNames.SourceText(LabelSourceText(candidate));
            if (caption.Length > 0)
            {
                return caption;
            }
        }

        return string.Empty;
    }

    // A label shows its trimmed value when the value has text, and its name otherwise.
    private static string LabelSourceText(CompactTreeNode label)
    {
        string trimmedValue = (label.Value ?? string.Empty).Trim();
        return trimmedValue.Length > 0 ? trimmedValue : label.Name;
    }

    // The unmapped list keeps each role and name pair once, in the order the pair was first seen.
    private void CountUnmapped(string role, string name)
    {
        int existing = _unmapped.FindIndex(candidate => candidate.Role == role && candidate.Name == name);
        if (existing < 0)
        {
            _unmapped.Add(new CompactUnmappedEntry(role, name, 1));
            return;
        }

        CompactUnmappedEntry entry = _unmapped[existing];
        _unmapped[existing] = entry with
        {
            Count = entry.Count + 1
        };
    }

    // A node's descendants are the following nodes that are indented deeper than the node itself.
    private int ChildrenEnd(int index)
    {
        int depth = _body[index].Depth;
        int end = index + 1;
        while (end < _body.Count && _body[end].Depth > depth)
        {
            end++;
        }

        return end;
    }

    // A fallback key is positional, so its binding carries the flag that tells an apply not to trust the name.
    private void RecordBinding(string bindingPath, CompactTreeNode node, CompactBoundMember boundMember, bool fallback)
    {
        _bindings[bindingPath] = new CompactMemberBinding(node.Id, boundMember, node.Enabled, fallback);
    }

    private static List<CompactMemberKey> ExtendPath(IReadOnlyList<CompactMemberKey> path, CompactMemberKey key)
    {
        List<CompactMemberKey> extended = new(path);
        extended.Add(key);
        return extended;
    }

    private static List<CompactMemberNote> DisabledNotes(CompactTreeNode node)
    {
        List<CompactMemberNote> notes = new();
        if (!node.Enabled)
        {
            notes.Add(CompactMemberNote.Disabled);
        }

        return notes;
    }

    private static string PropertyFallback(CompactMemberClass memberClass)
    {
        if (memberClass == CompactMemberClass.Secret)
        {
            return "password";
        }

        if (memberClass == CompactMemberClass.Number)
        {
            return "number";
        }

        if (memberClass == CompactMemberClass.Choice)
        {
            return "choice";
        }

        return "text";
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        JsonArray array = new();
        foreach (string value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }
}

// Use case: the builder and its classifier share one object level per namespace, which owns its members and the keys it has taken.
internal sealed class CompactNamespace
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public List<CompactDeclaredMember> Members { get; } = new();

    // A repeated identifier gets the next number after the base, so every key in one object level is unique.
    public CompactMemberKey AddKey(string baseText, string fallback)
    {
        CompactMemberKey first = BaseKey(baseText, fallback);
        string candidate = first.Name;
        int number = 1;
        while (_keys.Contains(candidate))
        {
            number++;
            candidate = first.Name + number.ToString(CultureInfo.InvariantCulture);
        }

        _keys.Add(candidate);
        return new CompactMemberKey(candidate, first.Quoted);
    }

    public void ReleaseKey(CompactMemberKey key)
    {
        _keys.Remove(key.Name);
    }

    // A label without letters or digits is kept as written and quoted; an empty label takes the fallback name.
    private static CompactMemberKey BaseKey(string baseText, string fallback)
    {
        string? identifier = CompactWindowStateNames.Identifier(baseText);
        if (identifier is not null)
        {
            return new CompactMemberKey(identifier, false);
        }

        if (baseText.Length > 0)
        {
            return new CompactMemberKey(baseText, true);
        }

        return new CompactMemberKey(fallback, false);
    }
}

// Use case: the builder asks for one class per native node; the classifier applies the ordered rules of the JS reference.
internal static class CompactWindowClassifier
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex MenuBarPattern = new("menu bar|menubar", MatchOptions);
    private static readonly Regex MenuPattern = new("menu", MatchOptions);
    private static readonly Regex ToggleRolePattern = new("check|radio|toggle", MatchOptions);
    private static readonly Regex MethodRolePattern = new("button|menu ?item|link", MatchOptions);
    private static readonly Regex StructuralFramePattern =
        new("panel|filler|pane|group|scroll|split|viewport|section|layered", MatchOptions);

    // Field roles come first, so a password field stays a secret even when its role also contains a text word.
    private static readonly CompactClassRule[] FieldRules =
    {
        Rule("separator", CompactMemberClass.Ignore),
        Rule(@"password|\Asecuretextfield\z", CompactMemberClass.Secret),
        Rule(@"\bEdit\b|entry|\A(?:textfield|textarea)\z", CompactMemberClass.Text),
        Rule("spin|slider", CompactMemberClass.Number),
        Rule("combo|popupbutton", CompactMemberClass.Choice),
        WholeRule("list|list box|table|tree|tree table|outline", CompactMemberClass.Collection)
    };

    private static readonly CompactClassRule[] LeafRules =
    {
        Rule("check|radio|toggle", CompactMemberClass.Toggle),
        Rule("button|menu ?item|link|^menu$|tab", CompactMemberClass.Method),
        WholeRule("label|static|statictext|text", CompactMemberClass.Label)
    };

    public static CompactMemberClass Classify(string role, string name, string? value, bool hasChildren)
    {
        CompactMemberClass? fieldClass = FirstMatch(FieldRules, role);
        if (fieldClass is not null)
        {
            return fieldClass.Value;
        }

        CompactMemberClass? containerClass = ClassifyContainer(role, hasChildren);
        if (containerClass is not null)
        {
            return containerClass.Value;
        }

        // An empty frame is layout only; it is ignored rather than reported as unmapped noise.
        if (IsEmptyLayoutFrame(role, name, value))
        {
            return CompactMemberClass.Ignore;
        }

        CompactMemberClass? leafClass = FirstMatch(LeafRules, role);
        return leafClass ?? CompactMemberClass.Unmapped;
    }

    // A leaf frame role without a name or value is structure only; a labelled frame still goes through the leaf rules.
    private static bool IsEmptyLayoutFrame(string role, string name, string? value)
    {
        bool isFrameRole = StructuralFramePattern.IsMatch(role);
        bool hasName = name.Trim().Length > 0;
        bool hasValue = (value ?? string.Empty).Trim().Length > 0;
        return isFrameRole && !hasName && !hasValue;
    }

    // A menu with children is a namespace; any other role with children is a plain container that passes its members on.
    private static CompactMemberClass? ClassifyContainer(string role, bool hasChildren)
    {
        if (MenuBarPattern.IsMatch(role))
        {
            return CompactMemberClass.Container;
        }

        if (hasChildren && MenuPattern.IsMatch(role))
        {
            return CompactMemberClass.Namespace;
        }

        // A native button or check box often keeps its caption as a child label, so its role decides before its children.
        if (hasChildren && ToggleRolePattern.IsMatch(role))
        {
            return CompactMemberClass.Toggle;
        }

        if (hasChildren && MethodRolePattern.IsMatch(role))
        {
            return CompactMemberClass.Method;
        }

        if (hasChildren)
        {
            return CompactMemberClass.Container;
        }

        return null;
    }

    private static CompactMemberClass? FirstMatch(CompactClassRule[] rules, string role)
    {
        foreach (CompactClassRule rule in rules)
        {
            if (rule.Pattern.IsMatch(role))
            {
                return rule.MemberClass;
            }
        }

        return null;
    }

    private static CompactClassRule Rule(string pattern, CompactMemberClass memberClass)
    {
        return new CompactClassRule(new Regex(pattern, MatchOptions), memberClass);
    }

    // A whole-role rule matches the entire role name, as the JS reference does with a full match.
    private static CompactClassRule WholeRule(string pattern, CompactMemberClass memberClass)
    {
        return new CompactClassRule(new Regex(@"\A(?:" + pattern + @")\z", MatchOptions), memberClass);
    }
}

// Use case: the classifier pairs one ordered role pattern with the member class it selects.
internal sealed record CompactClassRule(Regex Pattern, CompactMemberClass MemberClass);

// Use case: the builder assigns exactly one class to each native node; every class has one declaration rule.
internal enum CompactMemberClass
{
    Ignore,
    Secret,
    Text,
    Number,
    Choice,
    Collection,
    Container,
    Namespace,
    Toggle,
    Method,
    Label,
    Unmapped
}

// Use case: the declaration renders each member as one of these four kinds, and the state keeps the values of the properties.
internal enum CompactDeclaredKind
{
    Property,
    Collection,
    Method,
    Namespace
}

// Use case: a property is declared as a string or as a number, which decides the type name and the state value.
internal enum CompactValueType
{
    Text,
    Number
}

// Use case: a note is the comment after a declared member and names a control state the native tree can report.
internal enum CompactMemberNote
{
    Disabled,
    WriteOnly,
    Choice,
    Toggle
}

// Use case: a member key is the declared identifier, and Quoted tells whether the identifier must be written as a string.
internal sealed record CompactMemberKey(string Name, bool Quoted);

// Use case: one declared member; Children is set only for a namespace member, which nests further members.
internal sealed record CompactDeclaredMember(
    CompactDeclaredKind Kind,
    CompactMemberKey Key,
    IReadOnlyList<CompactMemberNote> Notes,
    CompactValueType ValueType,
    CompactNamespace? Children);

// Use case: one native role and name pair that the declaration cannot map, with how many nodes of it the window contains.
internal sealed record CompactUnmappedEntry(string Role, string Name, int Count);

// Use case: the bindings name the native row of each declared member, with the flags that an apply needs to act on that row.
// Fallback marks a key made from the member kind because the control has no name, so the key is positional.
public sealed record CompactMemberBinding(int NodeId, CompactBoundMember Member, bool Enabled, bool Fallback);

// Use case: the kinds of declared member that a binding can name; the wire name of each is its lower case word.
public enum CompactBoundMember
{
    Text,
    Secret,
    Number,
    Choice,
    Collection,
    Namespace,
    Method,
    Toggle
}

// Use case: Bind returns the bindings by member path in declaration order, or the reason it has none.
public sealed record CompactWindowBindingsAttempt(
    IReadOnlyDictionary<string, CompactMemberBinding>? Value,
    CompactWindowStateError? Error);
