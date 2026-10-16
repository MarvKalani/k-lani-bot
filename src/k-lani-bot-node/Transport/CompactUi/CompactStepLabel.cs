namespace KLaniBot.Transport.CompactUi;

// Use case: a stored function's reply names each step it ran from stored data alone, so a model can check a run without any typed text.
public static class CompactStepLabel
{
    // A write shows only its target: a password field may hold the text, and a parameter value is never stored in the recipe.
    public static string Of(CompactRecipeStep step)
    {
        var path = (step.Selector.ContainerPath ?? []).Select(container => (container.Name ?? "").Trim())
            .Where(name => name.Length > 0);
        string target = string.IsNullOrWhiteSpace(step.Selector.Name) ? step.Selector.Role.Name : step.Selector.Name.Trim();

        return CompactCaptureNames.Action(step.Action) + " " + string.Join(" > ", path.Append(target));
    }
}
