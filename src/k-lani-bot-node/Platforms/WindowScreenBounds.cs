using System.Text.Json.Serialization;

namespace KLaniBot.Platforms;

// Use case: the last-resort input route reads one window's rectangle by native handle, in the pixels take_screenshot returns, to
// refuse a point outside the window and a window that moved since the screenshot the coordinates came from.
public sealed record WindowScreenBounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);
