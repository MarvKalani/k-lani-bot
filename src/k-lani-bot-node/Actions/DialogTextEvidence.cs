using System.Text.Json.Serialization;

namespace KLaniBot.Actions;

// How much of a dialog's text the journal keeps next to an agent's decision. The words are the spelling in policy.json.
[JsonConverter(typeof(DialogTextEvidenceJsonConverter))]
public enum DialogTextEvidence
{
    // The SHA-256 of the whole text and its first 200 characters.
    [JsonStringEnumMemberName("excerpt")]
    Excerpt,

    // Only the SHA-256, for dialogs whose text must not be kept.
    [JsonStringEnumMemberName("hash_only")]
    HashOnly
}

// A number in place of a word would pick whatever setting has that position today, so only words are read.
public sealed class DialogTextEvidenceJsonConverter()
    : JsonStringEnumConverter<DialogTextEvidence>(namingPolicy: null, allowIntegerValues: false);
