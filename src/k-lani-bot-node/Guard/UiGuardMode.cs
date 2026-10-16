using System.Text.Json.Serialization;

namespace KLaniBot.Guard;

// The operator's choice of how far a principal with the "ui" scope reaches into the desktop. The words are
// the spelling in policy.json and on the --ui-guard option. A new principal is guarded unless the operator
// names desktop.
[JsonConverter(typeof(JsonStringEnumConverter<UiGuardMode>))]
public enum UiGuardMode
{
    // Only applications, windows and elements named by local rules (UiActionGuard).
    [JsonStringEnumMemberName("guarded")]
    Guarded,

    // The whole desktop within the principal's scopes, including raw keys, coordinates and screenshots.
    [JsonStringEnumMemberName("desktop")]
    Desktop
}
