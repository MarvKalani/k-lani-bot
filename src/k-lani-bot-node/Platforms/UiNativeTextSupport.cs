namespace KLaniBot.Platforms;

// Element input handlers probe a native setter before preparation; unknown support never proves that alternate input is safe.
public enum UiNativeTextSupport
{
    Unknown,
    Available,
    Unavailable
}
