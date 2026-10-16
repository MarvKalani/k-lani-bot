using KLaniBot.Actions;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Form setters validate current native support/context/options before input and validate complete values afterward.
public static class CompactFormPolicy
{
    public static bool Writable(CompactFormField field, bool writing)
    {
        return field.Metadata.Support == UiFormSupport.Available && field.Visible == CompactKnownBoolean.True
            && field.Enabled && (!writing || field.ReadOnly != CompactKnownBoolean.True);
    }

    public static bool TryPrepareSet(CompactFormField field, CompactFormValue value, string snapshotId,
        out CompactFormAttempt<CompactFormMutation> attempt)
    {
        if (!Writable(field, true))
        {
            attempt = new(null, CompactFormError.UnsupportedField);
            return false;
        }
        if (value.Kind == CompactFormValueKind.Text)
        {
            if (value.Text == null || value.Text.Length > 16000 || value.OptionId != null || value.OptionsRevision != null)
            {
                attempt = new(null, CompactFormError.InvalidArguments);
                return false;
            }
            if (field.Kind != CompactFormFieldKind.Text)
            {
                attempt = new(null, CompactFormError.LiteralChoice);
                return false;
            }
            attempt = new(new(Tool.TypeText, field.NativeId, snapshotId, value.Kind, value.Text,
                Text: value.Text, Context: field.Metadata.Context ?? ""), null);
            return true;
        }
        return TryOption(field, value, snapshotId, out attempt);
    }

    private static bool TryOption(CompactFormField field, CompactFormValue value, string snapshotId,
        out CompactFormAttempt<CompactFormMutation> attempt)
    {
        if (value.Kind != CompactFormValueKind.Option || value.OptionId == null || value.Text != null || value.OptionsRevision == null)
        {
            attempt = new(null, CompactFormError.InvalidArguments);
            return false;
        }
        if (field.Kind != CompactFormFieldKind.Choice || !field.Metadata.Choices.Complete || value.OptionsRevision != field.OptionsRevision)
        {
            attempt = new(null, CompactFormError.StaleOptions);
            return false;
        }
        UiChoiceOption? option = field.Options.FirstOrDefault(option => option.Id == value.OptionId && option.Enabled);
        if (option == null)
        {
            attempt = new(null, CompactFormError.OptionUnknownOrDisabled);
            return false;
        }
        attempt = new(new(Tool.ClickElement, field.NativeId, snapshotId, value.Kind, option.Label, OptionId: option.Id,
            OptionsRevision: field.OptionsRevision, Context: field.Metadata.Context), null);

        return true;
    }

    public static CompactFormValidation Validate(IReadOnlyList<CompactFormField> fields)
    {
        var missing = new List<CompactFormFieldReference>();
        var invalid = new List<CompactFormFieldReference>();
        var unknown = new List<CompactFormFieldReference>();
        foreach (CompactFormField field in fields)
        {
            if (field.Visible == CompactKnownBoolean.False)
            {
                if (field.Required == CompactKnownBoolean.True)
                {
                    unknown.Add(field.Reference);
                }
                continue;
            }
            if (field.Required == CompactKnownBoolean.True && string.IsNullOrEmpty(field.Value))
            {
                missing.Add(field.Reference);
            }
            if (field.Required == CompactKnownBoolean.Unknown || field.ReadOnly == CompactKnownBoolean.Unknown
                || field.Visible == CompactKnownBoolean.Unknown || field.Metadata.Support != UiFormSupport.Available)
            {
                unknown.Add(field.Reference);
            }
            ValidateChoice(field, invalid, unknown);
        }

        return new(missing.Count + invalid.Count + unknown.Count == 0, missing, invalid, unknown.Distinct().ToArray());
    }

    private static void ValidateChoice(CompactFormField field, List<CompactFormFieldReference> invalid,
        List<CompactFormFieldReference> unknown)
    {
        if (field.Kind != CompactFormFieldKind.Choice)
        {
            return;
        }
        if (field.Metadata.Choices.Support != UiFormSupport.Available || !field.Metadata.Choices.Complete)
        {
            unknown.Add(field.Reference);
        }
        else if (field.Value != null && !field.Options.Any(option => option.Label == field.Value))
        {
            invalid.Add(field.Reference);
        }
    }

    public static IReadOnlyList<CompactFormFieldReference> Invalidated(CompactFormField changed, IReadOnlyList<CompactFormField> current)
    {
        string[]? dependencies = changed.Metadata.Dependencies;

        return current.Where(field => dependencies == null || dependencies.Contains(field.Metadata.Field, StringComparer.Ordinal))
            .Select(field => field.Reference).ToArray();
    }
}
