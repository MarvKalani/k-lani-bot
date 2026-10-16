namespace KLaniBot.Platforms.Linux;

// How AtSpiBackend.Observe compares a window caption with the requested title. Contains keeps the search behaviour, where an
// empty title means every window; Exact picks the one window a handle named, so an empty title selects only untitled windows.
public enum CaptionMatch
{
    Contains,
    Exact
}
