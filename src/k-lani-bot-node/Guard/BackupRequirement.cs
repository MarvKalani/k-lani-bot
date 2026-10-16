using System.Text.Json.Serialization;

namespace KLaniBot.Guard;

// Whether a change needs a current backup first. The words are the spelling in policy.json. The administrator switches the
// requirement off explicitly, locally in the policy or remotely through the master; a missing setting means Required.
[JsonConverter(typeof(JsonStringEnumConverter<BackupRequirement>))]
public enum BackupRequirement
{
    // The first change of every session needs a current backup of the configured source.
    [JsonStringEnumMemberName("required")]
    Required,

    // The administrator decided that changes on this node need no backup check.
    [JsonStringEnumMemberName("off")]
    Off
}
