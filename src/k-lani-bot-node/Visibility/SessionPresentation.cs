using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Localization;
using KLaniBot.Transport;

namespace KLaniBot.Visibility;

// Who asks for access and why, as the person at the machine sees it. Client and purpose come from the AI client;
// master, node and rights from the node's own configuration. An empty field was not stated, and the display says so in
// the person's language.
public sealed record SessionPresentation(string Client, string Purpose, string Master, string Node, string Rights)
{
    public const string Header = ProtocolIdentity.PresentationHeader;

    private static readonly Dictionary<Scope, UiText> RightTexts = new()
    {
        [Scope.Observe] = UiText.RightObserve,
        [Scope.Ui] = UiText.RightUi,
        [Scope.InventoryRead] = UiText.RightInventoryRead,
        [Scope.FilesRead] = UiText.RightFilesRead,
        [Scope.FilesWrite] = UiText.RightFilesWrite,
        [Scope.Shell] = UiText.RightShell,
        [Scope.Clipboard] = UiText.RightClipboard,
        [Scope.Process] = UiText.RightProcess,
        [Scope.MacroWrite] = UiText.RightMacroWrite,
        [Scope.Eval] = UiText.RightEval,
        [Scope.BackupRead] = UiText.RightBackupRead,
        [Scope.BackupCreate] = UiText.RightBackupCreate,
        [Scope.BackupRestore] = UiText.RightBackupRestore
    };

    public string ShownClient => Shown(Client, UiText.UnnamedClient);
    public string ShownPurpose => Shown(Purpose, UiText.PurposeNotStated);
    public string ShownMaster => Shown(Master, UiText.NotStated);
    public string ShownNode => Shown(Node, UiText.ThisComputer);

    // Throws ArgumentException when a field is too long or contains control characters other than a line break.
    public static SessionPresentation Create(string? client, string? purpose, string master = "", string node = "", string rights = "")
    {
        return new SessionPresentation(
            Clean(client, 80),
            Clean(purpose, 500),
            Clean(master, 256),
            Clean(node, 80),
            Clean(rights, 256));
    }

    // Throws ArgumentException for a value outside the display contract.
    private static string Clean(string? value, int maximum)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        if (text.Length > maximum || text.Any(character => char.IsControl(character) && character != '\n'))
        {
            throw new ArgumentException("Session presentation exceeds the display contract.");
        }
        return text;
    }

    private static string Shown(string value, UiText whenNotStated)
    {
        return value.Length > 0 ? value : UiTexts.Get(whenNotStated);
    }

    public string Encode()
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));
    }

    // Throws ArgumentException for an oversized or invalid header.
    public static SessionPresentation Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Create(null, null);
        }
        if (value.Length > 2048)
        {
            throw new ArgumentException("Session header too large.");
        }
        SessionPresentation decoded = JsonSerializer.Deserialize<SessionPresentation>(Convert.FromBase64String(value))
            ?? throw new ArgumentException("Invalid session presentation.");
        // Master identity and rights are supplied by the node's trusted configuration,
        // never accepted from client-controlled labels.
        return Create(decoded.Client, decoded.Purpose);
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["client"] = Client,
            ["purpose"] = Purpose,
            ["master"] = Master,
            ["node"] = Node,
            ["rights"] = Rights
        };
    }

    public string DescribeRights()
    {
        string[] words = Rights.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return UiTexts.Get(UiText.SeeLocalPermissionProfile);
        }

        var descriptions = new List<string>();
        foreach (string word in words)
        {
            bool known = WireNames<Scope>.TryParse(word, out Scope scope) && RightTexts.ContainsKey(scope);
            descriptions.Add(known ? UiTexts.Get(RightTexts[scope]) : word);
        }
        return string.Join("; ", descriptions);
    }

    // Throws ArgumentException unless exactly the five fields are present.
    public static SessionPresentation FromJson(JsonObject value)
    {
        if (value.Count != 5)
        {
            throw new ArgumentException("Invalid session details.");
        }
        return Create(value["client"]?.ToString(), value["purpose"]?.ToString(), value["master"]?.ToString() ?? "",
            value["node"]?.ToString() ?? "", value["rights"]?.ToString() ?? "");
    }
}
