using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Localization;
using KLaniBot.Visibility;

namespace KLaniBot.Actions;

// Use case: while the person watches the agent in slow motion or paused, the assignment box names each step in plain words:
// what kind of step and which control, program or keys. Typed text, clipboard contents and commands are never shown, only
// how many characters were typed, because they may hold secrets.
internal static class StepDescriber
{
    public const int MaximumAnnouncedSteps = 10;

    private static readonly Dictionary<Tool, UiText> ConcreteActions = new()
    {
        [Tool.ListWindows] = UiText.ActionFindWindows,
        [Tool.GetUiTree] = UiText.ActionInspectControls,
        [Tool.GetChapters] = UiText.ActionListSections,
        [Tool.InspectSection] = UiText.ActionInspectSection,
        [Tool.FindElements] = UiText.ActionFindControl,
        [Tool.GetUiDelta] = UiText.ActionReadChanges,
        [Tool.TakeScreenshot] = UiText.ActionCaptureImage,
        [Tool.LaunchProcess] = UiText.ActionStartProgram,
        [Tool.FocusWindow] = UiText.ActionFocusWindow,
        [Tool.GetWindowGeometry] = UiText.ActionReadWindowState,
        [Tool.SetWindowGeometry] = UiText.ActionSetWindowState,
        [Tool.CloseWindow] = UiText.ActionCloseWindow,
        [Tool.ClickElement] = UiText.ActionActivateControl,
        [Tool.SilentClick] = UiText.ActionActivateControl,
        [Tool.TypeText] = UiText.ActionSetControlText,
        [Tool.VerifyElement] = UiText.ActionVerifyControl,
        [Tool.WaitElement] = UiText.ActionWaitControl
    };

    public static StepView Describe(Tool tool, JsonObject arguments, UiTargetIdentity? target)
    {
        string title = ActionTitle(tool, arguments);
        string detail = Detail(arguments, target);
        if (arguments["title"] is { } windowTitle && detail.Length == 0)
        {
            detail = windowTitle.ToString();
        }

        string description = detail.Length == 0 ? title : title + ": " + detail;

        return new(KindOf(ToolFeatures.Of(ToolContract.Get(tool))), description, Title: description);
    }

    private static string ActionTitle(Tool tool, JsonObject arguments)
    {
        if (tool == Tool.InspectApplication)
        {
            UiText title = arguments["area"]?.ToString() switch
            {
                "Status" => UiText.ActionReadProgramState,
                "Api" => UiText.ActionReadApi,
                "Selection" => UiText.ActionReadSelectedMail,
                "Folders" => UiText.ActionReadFolders,
                "Inbox" => UiText.ActionReadInbox,
                "MailPage" => UiText.ActionReadMailPage,
                "Mail" => UiText.ActionReadMailBody,
                _ => UiText.ActionReadVersion
            };
            return UiTexts.Get(title);
        }
        if (tool == Tool.OperateApplication)
        {
            return UiTexts.Format(UiText.ActionNativeOperation, arguments["operation"]?.ToString() ?? "",
                arguments["field"]?.ToString() ?? "");
        }

        return ConcreteActions.TryGetValue(tool, out UiText label) ? UiTexts.Get(label)
            : ToolContract.Get(tool)["description"]!.ToString();
    }

    public static StepView Describe(ToolFeature feature, JsonObject arguments, UiTargetIdentity? target)
    {
        return new StepView(KindOf(feature), Detail(arguments, target));
    }

    // A macro names its remaining steps from their contract entries, without asking the desktop.
    public static StepView DescribeMacroStep(JsonObject step)
    {
        string action = step["action"]?.ToString() ?? "";
        JsonObject arguments = step["args"] as JsonObject ?? new JsonObject();
        if (!ToolNames.TryParse(action, out Tool tool))
        {
            return new StepView(StepKind.Other, "");
        }
        return Describe(tool, arguments, null);
    }

    // The agent's own plan for the steps after this one, or null when it states none.
    public static IReadOnlyList<StepView>? Announced(JsonObject arguments)
    {
        if (arguments["next_steps"] is not JsonArray planned)
        {
            return null;
        }
        return planned.OfType<JsonValue>()
            .Select(value => value.TryGetValue(out string? text) ? text : "")
            .Where(text => text.Length > 0)
            .Take(MaximumAnnouncedSteps)
            .Select(text => new StepView(StepKind.Announced, StepText.Clean(text)))
            .ToArray();
    }

    private static string Detail(JsonObject arguments, UiTargetIdentity? target)
    {
        string subject = SubjectOf(arguments, target);
        bool typesText = arguments["text"] is JsonValue text && text.TryGetValue(out string? _);
        if (!typesText)
        {
            return subject;
        }
        string characters = UiTexts.Format(UiText.StepCharacters, arguments["text"]!.ToString().Length);
        return subject.Length == 0 ? characters : subject + ", " + characters;
    }

    private static string SubjectOf(JsonObject arguments, UiTargetIdentity? target)
    {
        if (target != null)
        {
            return target.Name.Length == 0 ? target.WindowTitle : target.Name + " (" + target.WindowTitle + ")";
        }
        if (arguments["name"] is JsonValue name)
        {
            return name.ToString();
        }
        if (arguments["path"] is JsonValue path)
        {
            return Path.GetFileName(path.ToString());
        }
        if (arguments["keys"] is JsonArray keys)
        {
            return string.Join("+", keys.Select(key => key?.ToString()));
        }
        return "";
    }

    private static StepKind KindOf(ToolFeature feature)
    {
        return feature switch
        {
            ToolFeature.SemanticInspect or ToolFeature.ApplicationObjects or ToolFeature.Windows or ToolFeature.Screenshot => StepKind.Read,
            ToolFeature.SemanticInvoke or ToolFeature.BackgroundInvoke => StepKind.Click,
            ToolFeature.TextInjection => StepKind.Type,
            ToolFeature.PhysicalInput => StepKind.Input,
            ToolFeature.WindowControl => StepKind.Window,
            ToolFeature.Process => StepKind.Launch,
            ToolFeature.Clipboard => StepKind.Clipboard,
            ToolFeature.Filesystem or ToolFeature.Backup => StepKind.Files,
            ToolFeature.Terminal => StepKind.Command,
            ToolFeature.Macros => StepKind.Macro,
            ToolFeature.Meta or ToolFeature.Fleet or ToolFeature.Classifier => StepKind.Other,
            _ => throw new UnreachableException()
        };
    }
}

// Thrown before a step reaches the desktop when the person paused the agent while it waited at the pace gate.
public sealed class PausedByPersonException() : InvalidOperationException(
    "The person at the machine paused the agent; nothing was performed. Observe again before the next step, and never rerun a "
    + "macro from its first step.");
