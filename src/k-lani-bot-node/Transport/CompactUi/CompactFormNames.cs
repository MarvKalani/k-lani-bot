using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Protocol failures retain their established exact form reason, with unknown effects explicitly separated from refusal before input.
public static class CompactFormNames
{
    public static string Error(CompactFormError error)
    {
        return error switch
        {
            CompactFormError.InvalidArguments => "Invalid form arguments.",
            CompactFormError.UnknownOrExpiredForm => "unknown_or_expired_form",
            CompactFormError.StaleContext => "stale_context",
            CompactFormError.UnknownOrStaleField => "unknown_or_stale_field",
            CompactFormError.FieldBoundExceeded => "Form scope exceeds its field bound.",
            CompactFormError.AmbiguousField => "ambiguous_field",
            CompactFormError.RequestedFieldUnknownOrAmbiguous => "Unknown or ambiguous requested form field.",
            CompactFormError.UnsupportedField => "unsupported_hidden_disabled_or_read_only_field",
            CompactFormError.OptionsUnsupported => "options_unsupported",
            CompactFormError.LiteralChoice => "Literal text is not a choice selection.",
            CompactFormError.StaleOptions => "stale_options",
            CompactFormError.OptionUnknownOrDisabled => "Unknown or disabled observed option.",
            CompactFormError.StaleCursor => "stale_cursor",
            CompactFormError.ScopeChanged => "The observed window instance was replaced or cannot be verified.",
            CompactFormError.ObservationUnavailable => "Native form observation is unavailable.",
            CompactFormError.Unconfirmed => "unconfirmed",
            _ => throw new UnreachableException()
        };
    }
}
