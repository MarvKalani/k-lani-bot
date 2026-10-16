using System.Text.Json.Serialization;

namespace KLaniBot.Platforms;

// Form callers distinguish unavailable native interfaces from currently observed, bounded information.
[JsonConverter(typeof(JsonStringEnumConverter<UiFormSupport>))]
public enum UiFormSupport
{
    Unsupported,
    Available
}

// Form callers retain the origin of metadata instead of interpreting labels as business constraints.
[JsonConverter(typeof(JsonStringEnumConverter<UiFieldMetadataSource>))]
public enum UiFieldMetadataSource
{
    Unsupported,
    AtSpiState,
    ApplicationDeclaration
}

// Capability discovery reports the independently supported native form routes, without claiming every field supports them.
public sealed record UiFormCapabilities(UiFormSupport Metadata, UiFormSupport Options, UiFormSupport Selection)
{
    public static UiFormCapabilities Unsupported
    {
        get;
    } = new(
        UiFormSupport.Unsupported, UiFormSupport.Unsupported, UiFormSupport.Unsupported);
}

// A freshly observed native choice has a session-bound opaque identity and its actual display label.
public sealed record UiChoiceOption(string Id, string Label, bool Enabled);

// Form readers distinguish complete option sets, absent interfaces and lists requiring an explicitly recorded opening action.
public sealed record UiChoiceMetadata(UiFormSupport Support, string Revision, UiChoiceOption[] Options,
    bool Complete, bool NeedsOpen, string? SelectedId)
{
    public static UiChoiceMetadata Unsupported { get; } = new(UiFormSupport.Unsupported, "", [], false, false, null);
}

// Form readers receive current native state and explicit unknown constraints. Runtime values are not reusable rules.
public sealed record UiFieldMetadata(UiFormSupport Support, UiFieldMetadataSource Source, bool? Enabled, bool? Visible, bool? ReadOnly,
    bool? Required, string? Context, string? Field, string[]? Dependencies, UiChoiceMetadata Choices)
{
    public static UiFieldMetadata Unsupported
    {
        get;
    } = new(UiFormSupport.Unsupported, UiFieldMetadataSource.Unsupported,
        null, null, null, null, null, null, null, UiChoiceMetadata.Unsupported);
}

// The native setter receives observed revision evidence along with the exact selected option; it never chooses a substitute.
public sealed record UiChoiceCommand(int ElementId, string OptionId, string OptionsRevision, string? Context);

// Accessibility providers may declare field constraints through this versioned, public description format.
public sealed record UiFieldDeclaration(
    string Format, string Field, string Context, bool? Required, bool? ReadOnly, string[]? Dependencies);
