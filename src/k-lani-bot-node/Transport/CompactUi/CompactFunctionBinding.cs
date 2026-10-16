namespace KLaniBot.Transport.CompactUi;

// Recorded selectors name their window by kind; the binding keeps the current window of each kind with its handle and lifetime.
public static class CompactFunctionBinding
{
    // Throws CompactNodeException for absent, replaced or version-unknown application instances. The proved instance is the
    // application process and its start time, so any of its current windows keeps the instance alive.
    public static CompactNativeWindow RequireInstance(IReadOnlyList<CompactNativeWindow> windows, CompactNativeWindow expected,
        CompactProgramIdentity application)
    {
        var candidates = OfInstance(windows, expected);
        if (expected.Started == null || candidates.Length == 0)
        {
            throw new CompactNodeException("The application instance was replaced or its lifetime is unknown.");
        }
        var current = candidates.FirstOrDefault(window => window.Handle == expected.Handle) ?? candidates[0];
        if (current.Build == "unknown" || current.Build != application.Build || current.ProcessName != expected.ProcessName)
        {
            throw new CompactNodeException("Native application version or variant does not match this cartridge.");
        }

        return current;
    }

    // Throws CompactNodeException when a step window is absent or ambiguous. A named window binds to its own kind and nothing else.
    public static IReadOnlyDictionary<CompactWindowKind, CompactNativeWindow> Bind(CompactFunctionDraft function,
        IReadOnlyList<CompactNativeWindow> windows, CompactNativeWindow instance, CompactNativeWindow? named)
    {
        var result = new Dictionary<CompactWindowKind, CompactNativeWindow>();
        foreach (CompactWindowKind kind in StepKinds(function))
        {
            result.Add(kind, RequireWindow(windows, instance, kind, named));
        }
        AddOutputs(function, windows, instance, named, result);

        return result;
    }

    // Throws CompactNodeException for an ambiguous output window; an output window that does not exist yet remains explicitly absent.
    public static void AddOutputs(CompactFunctionDraft function, IReadOnlyList<CompactNativeWindow> windows,
        CompactNativeWindow instance, CompactNativeWindow? named, IDictionary<CompactWindowKind, CompactNativeWindow> bindings)
    {
        foreach (CompactWindowKind kind in function.Outputs.Select(output => output.Selector.WindowKind).Distinct())
        {
            if (bindings.ContainsKey(kind))
            {
                continue;
            }
            if (named != null && named.Kind == kind)
            {
                bindings.Add(kind, named);
                continue;
            }
            CompactNativeWindow[] matches = OfKind(windows, instance, kind);
            if (matches.Length > 1)
            {
                throw new CompactNodeException("Recipe output window is ambiguous.");
            }
            if (matches.Length == 1)
            {
                bindings.Add(kind, matches[0]);
            }
        }
    }

    // Throws CompactNodeException when a bound window was replaced. A retired kind may be absent only while no window of that kind
    // remains in the instance; a reused handle or a new window of the same kind is still a replacement.
    public static IReadOnlyDictionary<CompactWindowKind, CompactNativeWindow> Refresh(
        IReadOnlyDictionary<CompactWindowKind, CompactNativeWindow> previous, IReadOnlyList<CompactNativeWindow> current,
        IReadOnlySet<CompactWindowKind>? retired = null)
    {
        var result = new Dictionary<CompactWindowKind, CompactNativeWindow>();
        foreach (var window in previous)
        {
            CompactNativeWindow[] matches = current.Where(candidate => candidate.ProcessId == window.Value.ProcessId
                && candidate.Handle == window.Value.Handle && candidate.Started != null
                && candidate.Started == window.Value.Started).ToArray();
            if (matches.Length != 1)
            {
                if (matches.Length == 0 && retired?.Contains(window.Key) == true && OfKind(current, window.Value, window.Key).Length == 0)
                {
                    continue;
                }
                throw new CompactNodeException("Recorded selector window instance was replaced or is absent.");
            }
            result.Add(window.Key, matches[0]);
        }

        return result;
    }

    // Throws CompactNodeException when no single current window of the kind is proved for a recipe step.
    private static CompactNativeWindow RequireWindow(IReadOnlyList<CompactNativeWindow> windows, CompactNativeWindow instance,
        CompactWindowKind kind, CompactNativeWindow? named)
    {
        if (named != null && named.Kind == kind)
        {
            return named;
        }
        CompactNativeWindow[] matches = OfKind(windows, instance, kind);
        if (matches.Length != 1)
        {
            throw new CompactNodeException(CompactWindowChoice.AmbiguousRefusal);
        }

        return matches[0];
    }

    // Steps decide which windows must be present; reads and outputs never add an input requirement.
    private static IReadOnlyList<CompactWindowKind> StepKinds(CompactFunctionDraft function)
    {
        return function.Steps.Where(step => step.Action != CompactRecipeAction.Read).Select(step => step.Selector.WindowKind)
            .Distinct().ToArray();
    }

    private static CompactNativeWindow[] OfKind(IReadOnlyList<CompactNativeWindow> windows, CompactNativeWindow instance,
        CompactWindowKind kind)
    {
        return OfInstance(windows, instance).Where(window => window.Kind == kind).ToArray();
    }

    private static CompactNativeWindow[] OfInstance(IReadOnlyList<CompactNativeWindow> windows, CompactNativeWindow instance)
    {
        return windows.Where(window => window.ProcessId == instance.ProcessId && window.Started != null
            && window.Started == instance.Started).ToArray();
    }
}
