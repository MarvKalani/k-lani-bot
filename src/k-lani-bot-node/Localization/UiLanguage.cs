using System.Text.Json.Serialization;

namespace KLaniBot.Localization;

// The language of what a person sees on the desktop. Messages to an AI client and to an administrator's terminal are
// always English and do not depend on it.
[JsonConverter(typeof(UiLanguageJsonConverter))]
public enum UiLanguage
{
    [JsonStringEnumMemberName("en")]
    English,

    [JsonStringEnumMemberName("de")]
    German
}

// policy.json names the language as a word; a number would pick whatever language has that position, so only words are read.
public sealed class UiLanguageJsonConverter() : JsonStringEnumConverter<UiLanguage>(namingPolicy: null, allowIntegerValues: false);
