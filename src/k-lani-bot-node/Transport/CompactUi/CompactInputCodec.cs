using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_input checks every step's fields before any node call. The schema bounds the sizes; this reader enforces which fields
// belong to which action and that a click comes first, alone, with a shot.
public static class CompactInputCodec
{
    private const string ClickFieldsRefusal = "A click takes x and y (and optionally button) and no keys or text.";
    private const string KeyFieldsRefusal = "A key step takes keys and nothing else.";
    private const string TypeFieldsRefusal = "A type step takes text and nothing else.";
    private const string ClickPlacementRefusal = "Only the first step may be a click, because its coordinates come from the screenshot.";
    private const string EmptyStepsRefusal = "Omit steps to take a screenshot; an empty steps list is not accepted.";

    // Throws ArgumentException for fields that do not belong to their action, a click after the first step, or a click without shot.
    public static CompactInputRequest Read(JsonObject arguments)
    {
        string reason = arguments["reason"]!.GetValue<string>();
        string? shot = arguments["shot"]?.GetValue<string>();
        var steps = new List<CompactInputStep>();
        if (arguments["steps"] is JsonArray list)
        {
            if (list.Count == 0)
            {
                throw new ArgumentException(EmptyStepsRefusal);
            }
            steps.AddRange(list.Select(step => Step(step!.AsObject())));
        }

        if (steps.Skip(1).Any(step => step.Action == CompactInputAction.Click))
        {
            throw new ArgumentException(ClickPlacementRefusal);
        }
        if (steps.Count > 0 && steps[0].Action == CompactInputAction.Click && string.IsNullOrEmpty(shot))
        {
            throw new ArgumentException(CompactInputNames.ShotRequiredRefusal);
        }

        return new CompactInputRequest(reason, shot, steps);
    }

    // Throws ArgumentException for fields that belong to another action.
    private static CompactInputStep Step(JsonObject step)
    {
        string action = step["action"]!.GetValue<string>();

        return action switch
        {
            "click" => Click(step),
            "key" => Key(step),
            "type" => Type(step),
            _ => throw new ArgumentException("Unsupported ui_input action.")
        };
    }

    // Throws ArgumentException for a click without both coordinates or with keys or text.
    private static CompactInputStep Click(JsonObject step)
    {
        if (step["x"] == null || step["y"] == null || step.ContainsKey("keys") || step.ContainsKey("text"))
        {
            throw new ArgumentException(ClickFieldsRefusal);
        }
        int x = step["x"]!.GetValue<int>();
        int y = step["y"]!.GetValue<int>();
        CompactPointerButton button = Button(step["button"]?.GetValue<string>());

        return new CompactInputStep(CompactInputAction.Click, x, y, button, [], "");
    }

    // Throws ArgumentException for missing or oversized key names or for fields of another action.
    private static CompactInputStep Key(JsonObject step)
    {
        bool foreign = step.ContainsKey("x") || step.ContainsKey("y") || step.ContainsKey("button") || step.ContainsKey("text");
        if (foreign || step["keys"] is not JsonArray keys)
        {
            throw new ArgumentException(KeyFieldsRefusal);
        }
        string[] names = keys.Select(key => key!.GetValue<string>()).ToArray();
        if (names.Any(name => name.Length is < 1 or > CompactUiStepCodec.MaximumKeyCharacters))
        {
            throw new ArgumentException(KeyFieldsRefusal);
        }

        return new CompactInputStep(CompactInputAction.Key, 0, 0, CompactPointerButton.Left, names, "");
    }

    // Throws ArgumentException for a missing text or for fields of another action.
    private static CompactInputStep Type(JsonObject step)
    {
        bool foreign = step.ContainsKey("x") || step.ContainsKey("y") || step.ContainsKey("button") || step.ContainsKey("keys");
        if (foreign || step["text"] == null)
        {
            throw new ArgumentException(TypeFieldsRefusal);
        }
        string text = step["text"]!.GetValue<string>();

        return new CompactInputStep(CompactInputAction.Type, 0, 0, CompactPointerButton.Left, [], text);
    }

    // Throws ArgumentException for a button word outside the schema's enum.
    private static CompactPointerButton Button(string? word)
    {
        return word switch
        {
            null or "left" => CompactPointerButton.Left,
            "right" => CompactPointerButton.Right,
            "double" => CompactPointerButton.Double,
            _ => throw new ArgumentException(ClickFieldsRefusal)
        };
    }
}
