using System.Text.Json.Serialization;

namespace KLaniBot.Actions;

// What happens when the local trace of executed steps is full. The words are the spelling in policy.json.
[JsonConverter(typeof(JsonStringEnumConverter<TraceRetention>))]
public enum TraceRetention
{
    // The oldest step moves to the archive, and the move is written to the journal with the digest of
    // every file, so a displaced step can still be proven to have existed.
    [JsonStringEnumMemberName("rotate")]
    Rotate,

    // Recording stops with a visible error until the operator archives the traces. For operators who
    // want the guarantee that nothing is ever moved or deleted without them.
    [JsonStringEnumMemberName("stop")]
    Stop
}
