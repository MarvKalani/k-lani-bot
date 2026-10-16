#if !WINDOWS
using System.Text.Json.Serialization;

namespace KLaniBot.Platforms.Linux;

// Linux foreground input matches an accessibility window to X11 geometry when their titles differ.
// The same screen bounds also describe observed controls at the JSON boundary.
internal sealed record AccessibleScreenBounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);
#endif
