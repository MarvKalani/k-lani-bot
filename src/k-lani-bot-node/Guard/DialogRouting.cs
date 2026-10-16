using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KLaniBot.Common;

namespace KLaniBot.Guard;

// Use case: the agent asks to operate a dialog, and the advisor has sorted that dialog into one of these classes from
// what the node itself read. The administrator's DialogDeciders say who handles each class.
[JsonConverter(typeof(DialogClassJsonConverter))]
public enum DialogClass
{
    // An ordinary question about the program's own work, such as saving or closing.
    [JsonStringEnumMemberName("routine")]
    Routine,

    // Trusting or bypassing something security relevant: a certificate, a credential, a permission, a payment.
    [JsonStringEnumMemberName("security_exception")]
    SecurityException,

    // Deleting, discarding or irreversibly replacing data.
    [JsonStringEnumMemberName("data_loss")]
    DataLoss,

    // No advisor, an advisor in doubt, or an answer outside these classes.
    [JsonStringEnumMemberName("unclear")]
    Unclear
}

// Who answers a dialog of a class: the agent with its recorded decision, the person at the machine by hand, or nobody.
[JsonConverter(typeof(DialogDeciderJsonConverter))]
public enum DialogDecider
{
    [JsonStringEnumMemberName("agent")]
    Agent,

    // The node refuses the agent's action and leaves the dialog to the person at the machine.
    [JsonStringEnumMemberName("person")]
    Person,

    [JsonStringEnumMemberName("blocked")]
    Blocked
}

// A number in place of a word would name whatever class has that position today, so only words are read.
public sealed class DialogClassJsonConverter() : JsonStringEnumConverter<DialogClass>(namingPolicy: null, allowIntegerValues: false);

// A number in place of a word would name whatever decider has that position today, so only words are read.
public sealed class DialogDeciderJsonConverter() : JsonStringEnumConverter<DialogDecider>(namingPolicy: null, allowIntegerValues: false);

// The administrator's answer to "who handles this kind of dialog", read from policy.json. Giving every class to the agent
// is full access; the defaults leave everything but routine questions to the person at the machine.
public sealed record DialogDeciders
{
    [JsonPropertyName("routine")] public DialogDecider Routine { get; init; } = DialogDecider.Agent;
    [JsonPropertyName("securityException")] public DialogDecider SecurityException { get; init; } = DialogDecider.Person;
    [JsonPropertyName("dataLoss")] public DialogDecider DataLoss { get; init; } = DialogDecider.Person;
    [JsonPropertyName("unclear")] public DialogDecider Unclear { get; init; } = DialogDecider.Person;

    public bool IsValid()
    {
        return Enum.IsDefined(Routine) && Enum.IsDefined(SecurityException) && Enum.IsDefined(DataLoss) && Enum.IsDefined(Unclear);
    }

    public DialogDecider For(DialogClass dialogClass)
    {
        return dialogClass switch
        {
            DialogClass.Routine => Routine,
            DialogClass.SecurityException => SecurityException,
            DialogClass.DataLoss => DataLoss,
            DialogClass.Unclear => Unclear,
            _ => throw new UnreachableException()
        };
    }
}

// What the node concluded about one dialog before the agent's action on it, and the evidence it concluded it from.
public sealed record DialogRouting(DialogClass Class, DialogDecider Decider, JsonObject Evidence);

// Thrown before dispatch when the administrator leaves a dialog's class to the person at the machine or blocks it.
public sealed class DialogRoutingException(DialogRouting routing) : UnauthorizedAccessException(MessageFor(routing))
{
    public DialogRouting Routing => routing;

    private static string MessageFor(DialogRouting routing)
    {
        string dialogClass = WireNames<DialogClass>.Of(routing.Class);
        return routing.Decider switch
        {
            DialogDecider.Person => "This " + dialogClass + " dialog is left to the person at the machine; nothing was performed. "
                + "Hand control back instead of operating it.",
            DialogDecider.Blocked => "The administrator does not allow operating " + dialogClass + " dialogs; nothing was performed.",
            DialogDecider.Agent => throw new UnreachableException(),
            _ => throw new UnreachableException()
        };
    }
}
