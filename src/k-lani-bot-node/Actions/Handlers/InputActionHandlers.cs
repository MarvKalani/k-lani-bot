using System.Text.Json.Nodes;
using KLaniBot.Guard;
using KLaniBot.Platforms;

namespace KLaniBot.Actions.Handlers;

// Use case: an AI clicks, types and presses keys. This is the group that can change things in a program
// the operator did not write, so every input that targets an element goes through one sequence: prepare
// the element, check scope, approval and guard once more, mark that the operating system may now be
// changed, and only then send. A foreground click moves the shared mouse; in background mode or under
// the guard the input goes to the element itself.
internal sealed class InputActionHandlers
{
    private readonly HandlerServices _services;

    public InputActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.ClickElement, ClickElement);
        add(Tool.SilentClick, SilentClick);
        add(Tool.MouseClick, MouseClick);
        add(Tool.TypeText, TypeText);
        add(Tool.MouseMove, MoveMouse);
        add(Tool.DragAndDrop, DragAndDrop);
        add(Tool.Hotkey, PressKeys);
    }

    private object ClickElement(ActionCall call)
    {
        int id = call.Arguments["id"]?.GetValue<int>() ?? 0;
        int autoWait = call.Arguments["auto_wait_ms"]?.GetValue<int?>() ?? 3000;
        if (call.Arguments["choice_action"] != null)
        {
            return ClickChoice(call, id);
        }
        bool focusOnly = call.Arguments["focus_only"]?.GetValue<bool>() ?? false;
        var (ok, message) = SendToElement(call, id, true, service => focusOnly ? service.FocusElement(id)
            : _services.UsesSemanticInput(call.Caller) ? service.InvokeBackgroundElement(id) : service.ClickElement(id, autoWait));
        return new
        {
            success = ok,
            message
        };
    }

    private object ClickChoice(ActionCall call, int id)
    {
        string action = call.Arguments["choice_action"]!.ToString();
        var command = new UiChoiceCommand(id, call.Arguments["option_id"]?.ToString() ?? "",
            call.Arguments["options_revision"]?.ToString() ?? "", call.Arguments["field_context"]?.ToString());
        if (action is not ("open" or "select")
            || action == "select" && (command.OptionId.Length == 0 || command.OptionsRevision.Length == 0))
        {
            return new
            {
                success = false,
                message = "Invalid native choice arguments."
            };
        }

        var result = SendFormInput(call, id, service => ValidateChoice(service, command, action), service =>
            action == "open" ? service.OpenChoices(id) : service.SelectChoice(command));
        return new
        {
            success = result.Success,
            message = result.Message
        };
    }

    private static UiOutcome ValidateChoice(IUiService service, UiChoiceCommand command, string action)
    {
        var metadata = service.ReadFieldMetadata(command.ElementId);
        if (metadata.Support != UiFormSupport.Available || metadata.Visible != true || metadata.Enabled != true
            || metadata.Choices.Support != UiFormSupport.Available)
        {
            return new(false, "unsupported_or_hidden_choice");
        }
        if (action == "select" && (metadata.ReadOnly == true || metadata.Context != command.Context
            || metadata.Choices.Revision != command.OptionsRevision
            || !metadata.Choices.Options.Any(option => option.Id == command.OptionId && option.Enabled)))
        {
            return new(false, "stale_or_unwritable_choice");
        }

        return new(true, "Native choice preflight passed.");
    }

    private UiOutcome SendFormInput(ActionCall call, int id, Func<IUiService, UiOutcome> validate, Func<IUiService, UiOutcome> send)
    {
        return _services.Ui.GuardedInput(() => ValidateInputImmediatelyBeforeDispatch(call), service =>
        {
            var preflight = validate(service);
            if (!preflight.Success)
            {
                return preflight;
            }
            PrepareElementInput(service, call, id);
            call.MarkBackendMayHaveMutated();
            return send(service);
        });
    }

    private object SilentClick(ActionCall call)
    {
        int id = call.Arguments["id"]?.GetValue<int>() ?? 0;
        var (ok, message) = SendToElement(call, id, true, service =>
            _services.UsesSemanticInput(call.Caller) ? service.InvokeBackgroundElement(id) : service.SilentClick(id));
        return new
        {
            success = ok,
            message
        };
    }

    // A click at a point needs no element; only a click on an element is prepared like the others.
    private object MouseClick(ActionCall call)
    {
        int id = call.Arguments["id"]?.GetValue<int>() ?? 0;
        string button = call.Arguments["button"]?.ToString() ?? "left";
        int? x = call.Arguments["x"]?.GetValue<int?>();
        int? y = call.Arguments["y"]?.GetValue<int?>();
        var (ok, message, info) = SendToElement(call, id, id > 0, service => service.MouseClick(id, button, x, y));
        return new
        {
            success = ok,
            message,
            clickInfo = info
        };
    }

    private object TypeText(ActionCall call)
    {
        int id = call.Arguments["id"]?.GetValue<int>() ?? 0;
        string text = call.Arguments["text"]?.ToString() ?? "";
        int autoWait = call.Arguments["auto_wait_ms"]?.GetValue<int?>() ?? 3000;
        if (call.Arguments["field_context"] != null)
        {
            if (call.Arguments["clear"]?.GetValue<bool>() == false || call.Arguments["enter"]?.GetValue<bool>() == true)
            {
                return new
                {
                    success = false,
                    message = "Form text input requires literal replacement without Enter."
                };
            }
            var outcome = SendFormInput(call, id, service => ValidateFormText(service, id, call.Arguments["field_context"]!.ToString()),
                service => service.SetGuardedText(id, text));
            return new
            {
                success = outcome.Success,
                message = outcome.Message
            };
        }
        return SendTextToElement(call, id, text, autoWait);
    }

    private object SendTextToElement(ActionCall call, int id, string text, int autoWait)
    {
        return _services.Ui.GuardedInput(() => ValidateInputImmediatelyBeforeDispatch(call), service =>
            DispatchTextToElement(service, call, id, text, autoWait));
    }

    private object DispatchTextToElement(IUiService service, ActionCall call, int id, string text, int autoWait)
    {
        if (service.ProbeNativeTextSupport(id) == UiNativeTextSupport.Unavailable)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["message"] = "Target has no editable text interface.",
                ["outcome"] = ActionOutcome.NotDispatched.ToWireName()
            };
        }
        PrepareElementInput(service, call, id);
        call.MarkBackendMayHaveMutated();
        UiOutcome result = _services.UsesSemanticInput(call.Caller) ? service.SetGuardedText(id, text)
            : service.TypeText(id, text, call.Arguments["clear"]?.GetValue<bool>() ?? true,
                call.Arguments["enter"]?.GetValue<bool>() ?? false, autoWait);

        return new
        {
            success = result.Success,
            message = result.Message
        };
    }

    private static UiOutcome ValidateFormText(IUiService service, int id, string context)
    {
        var metadata = service.ReadFieldMetadata(id);
        if (metadata.Support != UiFormSupport.Available || metadata.Visible != true || metadata.Enabled != true || metadata.ReadOnly == true
            || (metadata.Context ?? "") != context)
        {
            return new(false, "stale_or_unwritable_field");
        }

        return new(true, "Native field preflight passed.");
    }

    private object MoveMouse(ActionCall call)
    {
        var (ok, message) = _services.Ui.MouseMove(call.Arguments["x"]!.GetValue<int>(), call.Arguments["y"]!.GetValue<int>());
        return new
        {
            success = ok,
            message
        };
    }

    private object DragAndDrop(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        var (ok, message) = _services.Ui.DragAndDrop(
            arguments["from_x"]!.GetValue<int>(),
            arguments["from_y"]!.GetValue<int>(),
            arguments["to_x"]!.GetValue<int>(),
            arguments["to_y"]!.GetValue<int>());
        return new
        {
            success = ok,
            message
        };
    }

    private object PressKeys(ActionCall call)
    {
        string[] keys = (call.Arguments["keys"] as JsonArray)!.Select(key => key!.ToString()).ToArray();
        var (ok, message) = _services.Ui.KeyChord(keys);
        return new
        {
            success = ok,
            message
        };
    }

    // The sequence all element inputs share. The backend runs it on its own thread, and the checks run
    // there as well, immediately before the input leaves the node.
    private T SendToElement<T>(ActionCall call, int elementId, bool prepareElement, Func<IUiService, T> send)
    {
        return _services.Ui.GuardedInput(
            () => ValidateInputImmediatelyBeforeDispatch(call),
            service =>
            {
                if (prepareElement)
                {
                    PrepareElementInput(service, call, elementId);
                }
                call.MarkBackendMayHaveMutated();
                return send(service);
            });
    }

    // Dialog check and window focus for the element, then the last look at scope, approval and guard.
    // A recorded decision is what allows an input into a modal dialog. The dispatcher decides which decisions count.
    private void PrepareElementInput(IUiService service, ActionCall call, int elementId)
    {
        bool decisionRecorded = call.DecisionRecorded;
        service.PrepareElementInteraction(elementId, _services.WindowInteraction, _services.ModalDialogs, decisionRecorded);
        ValidateInputImmediatelyBeforeDispatch(call);
    }

    // Called right before an input leaves the node. Scope, local approval and guard are checked once more
    // because the person at the machine can revoke the session, and the operator can edit the policy,
    // between admission and the moment the click or key is actually sent.
    private void ValidateInputImmediatelyBeforeDispatch(ActionCall call)
    {
        _services.Policy.Authorize(call.Caller, Scope.Ui);
        _services.RequireApproval(call.Caller);
        PrincipalPolicy principal = _services.Policy.Get(call.Caller.PrincipalId);
        UiActionGuard.Authorize(_services.Policy, principal, _services.Ui, call.Tool, call.Arguments);
    }
}
