using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KLaniBot.Actions;
using KLaniBot.Visibility;

namespace KLaniBot.Guard;

// Whether the agent may bring its target window to the front, chosen by the person at the machine in the tray
// menu ("without mouse/keyboard"). In background mode the agent acts only through the accessibility interface
// on the element itself, so it neither raises a window nor types into whatever window has the focus.
[JsonConverter(typeof(JsonStringEnumConverter<WindowInteractionMode>))]
public enum WindowInteractionMode
{
    // The target window is raised before input, and input that needs the shared keyboard or mouse is allowed.
    Foreground,

    // Only semantic clicks and replacing a field's text; the target window stays where it is.
    BackgroundSemantic
}

// Whether the agent may operate modal dialogs, chosen in the tray menu. See ModalDialogPolicy for the rule.
[JsonConverter(typeof(JsonStringEnumConverter<ModalDialogMode>))]
public enum ModalDialogMode
{
    // A target inside a modal dialog stops the action, so a person can look at the dialog first.
    Pause,

    // The agent may operate the dialog if it records its decision with the request (AgentDecision).
    AllowInteraction
}

// Refuses the tools that would need the shared mouse, keyboard or foreground while background mode is chosen.
// It runs with the authorization phase, before consent is asked, so such a request is refused without a
// consent question. Tools without a semantic path are refused instead of falling back to physical input.
public static class WindowInteractionPolicy
{
    // Throws UnauthorizedAccessException when background mode cannot perform the action without the shared keyboard or mouse.
    public static void ValidateAction(WindowInteractionMode mode, Tool action, JsonObject args)
    {
        if (mode != WindowInteractionMode.BackgroundSemantic)
        {
            return;
        }
        var contract = ToolContract.Get(action);
        bool mutates = contract["mutates"]!.GetValue<bool>();
        ToolFeature feature = ToolFeatures.Of(contract);
        if (mutates && (AccessVisibility.NeedsBorder(action) || feature == ToolFeature.Process)
            && action is not (Tool.ClickElement or Tool.SilentClick or Tool.TypeText or Tool.RunMacro or Tool.OperateApplication))
        {
            throw new UnauthorizedAccessException(
                "Background mode: this action needs foreground access. Change the local window control setting.");
        }
        if (action == Tool.TypeText && (args["clear"]?.GetValue<bool>() == false || args["enter"]?.GetValue<bool>() == true))
        {
            // Appending and Enter would otherwise silently fall back to shared keyboard input.
            throw new UnauthorizedAccessException("Background mode: text can only replace the field value, without Enter.");
        }
    }
}
