using System.Globalization;
using KLaniBot.Localization;

namespace KLaniBot.Visibility;

// The WPF summary and copy export use the same source-labelled measurements, without treating node bytes as model tokens.
public static class StepEfficiencyText
{
    public static string Format(StepEfficiencySnapshot snapshot)
    {
        string history = string.Format(CultureInfo.InvariantCulture, UiTexts.Get(UiText.EfficiencyHistory),
            snapshot.Entries, snapshot.Completed, snapshot.Failed, snapshot.Uncertain);
        string measured = string.Format(CultureInfo.InvariantCulture, UiTexts.Get(UiText.EfficiencyMeasurements),
            snapshot.MeasuredEntries, snapshot.Entries, Number(snapshot.ArgumentBytes), Number(snapshot.ResultBytes),
            Number(snapshot.ElapsedMilliseconds));

        return history + Environment.NewLine + measured + Environment.NewLine + UiTexts.Get(UiText.EfficiencyModelUnknown);
    }

    private static string Number(long? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? UiTexts.Get(UiText.EfficiencyUnknown);
    }
}
