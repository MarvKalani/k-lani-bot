using System.Text.Json.Nodes;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// A recipe uses only a fresh unique semantic target; an unnamed target is written only as a bound plain text entry.
public static class CompactRecipeTarget
{
    // The JS desktop module refuses with the same two sentences, so both twins name each situation identically.
    public const string UnnamedRefusal = "A control without a name or automation ID can only take a text write into a text field "
        + "bound to its window; choose a named control.";
    public const string UnnamedTextRefusal = "This unnamed text field has no native text interface; it cannot be written safely.";

    // Throws CompactNodeException for refused unnamed targets, absent, ambiguous, wrong-type or disabled targets.
    public static JsonObject Require(CompactNativeObservation observation, CompactStoredSelector selector, CompactNativeWindow instance,
        CompactRecipeAction action)
    {
        RequireNamedTarget(selector, action);
        if (!CompactTargetResolver.TryResolve(observation.Nodes, selector.Target(instance.ProcessId), true, out var target))
        {
            throw new CompactNodeException(CompactTreeNames.Error(target.Error!.Value));
        }
        RequireWindowChild(observation.Nodes, selector, target.Value);

        return new JsonObject { ["id"] = target.Value, ["snapshot_id"] = observation.Snapshot };
    }

    // Throws CompactNodeException for refused unnamed targets, absent, ambiguous or wrong-type targets. A disabled match passes only
    // for a step that an earlier recorded step enables; the step's own dispatch still uses Require.
    public static void RequirePresent(CompactNativeObservation observation, CompactStoredSelector selector, CompactNativeWindow instance,
        CompactRecipeAction action)
    {
        if (!TryPresent(observation, selector, instance, action, out var failure))
        {
            throw new CompactNodeException(failure!.Kind == CompactTargetFailureKind.UnnamedRefused ? UnnamedRefusal
                : CompactTreeNames.Error(failure.Tree!.Value));
        }
    }

    // The presence rule of preflight without an exception, so a build-drift report can name the reason per step.
    public static bool TryPresent(CompactNativeObservation observation, CompactStoredSelector selector, CompactNativeWindow instance,
        CompactRecipeAction action, out CompactTargetFailure? failure)
    {
        failure = null;
        if (IsUnnamed(selector) && !CanWriteUnnamed(selector, action))
        {
            failure = new CompactTargetFailure(CompactTargetFailureKind.UnnamedRefused, null);
            return false;
        }
        if (!CompactTargetResolver.TryFindUnique(observation.Nodes, selector.Target(instance.ProcessId), true, out var target))
        {
            failure = new CompactTargetFailure(CompactTargetFailureKind.Tree, target.Error);
            return false;
        }

        return true;
    }

    // Throws CompactNodeException for an unnamed target that the action may not write. The resolver still requires one exact match.
    private static void RequireNamedTarget(CompactStoredSelector selector, CompactRecipeAction action)
    {
        if (IsUnnamed(selector) && !CanWriteUnnamed(selector, action))
        {
            throw new CompactNodeException(UnnamedRefusal);
        }
    }

    // Only a text write into a plain text entry with a known container path may reach an unnamed target. The stored selector always
    // names its window kind, so the owning window is bound by construction; the window caption is never consulted.
    private static bool CanWriteUnnamed(CompactStoredSelector selector, CompactRecipeAction action)
    {
        return action == CompactRecipeAction.Write && CompactRoleVocabulary.IsPlainTextEntry(selector.Role.Name)
            && selector.ContainerPath != null;
    }

    // A control with neither a name nor an automation ID is identified only by its window and container path.
    private static bool IsUnnamed(CompactStoredSelector selector)
    {
        return string.IsNullOrEmpty(selector.Name) && string.IsNullOrEmpty(selector.AutoId);
    }

    // Throws CompactNodeException for an unnamed field that would take the keyboard route; a paste could land in another control.
    public static void RequireNamedForKeyboard(CompactStoredSelector selector)
    {
        if (IsUnnamed(selector))
        {
            throw new CompactNodeException(UnnamedTextRefusal);
        }
    }

    // Throws CompactNodeException for an unnamed element whose empty container path is not proven by a window row in the tree.
    private static void RequireWindowChild(IReadOnlyList<CompactTreeNode> nodes, CompactStoredSelector selector, int identifier)
    {
        if (!IsUnnamed(selector) || selector.ContainerPath!.Count > 0)
        {
            return;
        }
        CompactTreeNode element = nodes.Single(node => node.Id == identifier);
        if (!nodes.Any(window => CompactTreeScope.IsWindow(window.Role) && CompactTreeScope.Contains(nodes, window, element)))
        {
            throw new CompactNodeException(UnnamedRefusal);
        }
    }

    // Throws CompactNodeException when the current selected option or native field interface is unavailable, incomplete or ambiguous.
    public static void Select(JsonObject arguments, CompactNativeObservation observation, string expected)
    {
        int identifier = arguments["id"]!.GetValue<int>();
        if (!observation.Metadata.TryGetValue(identifier, out var field) || field.Support != UiFormSupport.Available
            || field.Enabled != true || field.Visible != true || field.ReadOnly == true
            || field.Choices.Support != UiFormSupport.Available || !field.Choices.Complete)
        {
            throw new CompactNodeException("Current native option is unavailable or ambiguous.");
        }
        UiChoiceOption[] matches = field.Choices.Options.Where(option => option.Label == expected && option.Enabled).ToArray();
        if (matches.Length != 1)
        {
            throw new CompactNodeException("Current native option is unavailable or ambiguous.");
        }
        arguments["choice_action"] = "select";
        arguments["option_id"] = matches[0].Id;
        arguments["options_revision"] = field.Choices.Revision;
        if (field.Context != null)
        {
            arguments["field_context"] = field.Context;
        }
    }
}

// Preflight refuses a stored target either by the unnamed-target rule or by the resolver's own reason.
public enum CompactTargetFailureKind
{
    UnnamedRefused,
    Tree
}

// Why a stored target is not present; Tree is set only for a resolver refusal.
public sealed record CompactTargetFailure(CompactTargetFailureKind Kind, CompactTreeError? Tree);
