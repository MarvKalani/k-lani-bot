using System.Diagnostics;
using System.Text;
using KLaniBot.Fleet;
using KLaniBot.Localization;

namespace KLaniBot.ControllerTray;

// How a tray menu line behaves: a bold-ish group title, a line that only informs, a line that runs a command, or a divider.
internal enum ControllerTrayEntryKind
{
    Heading,
    Information,
    Command,
    Separator
}

// What a command line of the controller tray does.
internal enum ControllerTrayAction
{
    PauseClient,
    ResumeClient,
    SwitchBotOn,
    SwitchBotOff,
    CloseTray
}

// The tray icon's state: bot on and idle, a client working, bot switched off, or no master to ask.
internal enum ControllerTrayIcon
{
    On,
    Working,
    Off,
    MasterMissing
}

// A command the person chose in the tray. Instance names the client for pause and resume and is empty otherwise.
internal sealed record ControllerTrayCommand(ControllerTrayAction Action, string Instance)
{
    // The line sent over the master's administration pipe; null for closing the tray, which stays local.
    public string? MasterCommand()
    {
        return Action switch
        {
            ControllerTrayAction.PauseClient => "client-pause " + Instance,
            ControllerTrayAction.ResumeClient => "client-resume " + Instance,
            ControllerTrayAction.SwitchBotOn => "bot on",
            ControllerTrayAction.SwitchBotOff => "bot off",
            ControllerTrayAction.CloseTray => null,
            _ => throw new UnreachableException()
        };
    }
}

// One menu line. Indented lines belong to the node or client line above them.
internal sealed record ControllerTrayEntry(ControllerTrayEntryKind Kind, string Text, ControllerTrayCommand? Command, bool Indented);

// Everything a native tray draws: icon state, hover text and the menu lines in order.
internal sealed record ControllerTrayView(ControllerTrayIcon Icon, string Tooltip, IReadOnlyList<ControllerTrayEntry> Entries)
{
    // Equal stamps draw the same menu, so a host rebuilds only when something the person sees changed.
    public string Stamp()
    {
        var stamp = new StringBuilder(Icon.ToString()).Append('\n').Append(Tooltip);
        foreach (ControllerTrayEntry entry in Entries)
        {
            stamp.Append('\n').Append(entry.Kind).Append(entry.Indented ? " > " : " ").Append(entry.Text);
        }
        return stamp.ToString();
    }
}

// Use case: the controller tray turns the master's controller-status (or the absence of a master) into the menu the person
// reads, in their language, with one pause or resume line per MCP client and the bot's on/off switch. It has no native
// dependency, so the tests check the menu without a desktop and every platform's tray draws the same lines.
internal static class ControllerTrayMenu
{
    // Status is null when the master did not answer; the registered nodes from nodes.json are then shown with unknown state.
    public static ControllerTrayView Build(ControllerStatus? status, IReadOnlyList<string> registeredNodes, string lastFailure)
    {
        var entries = new List<ControllerTrayEntry>();
        if (lastFailure.Length > 0)
        {
            entries.Add(Information(UiTexts.Format(UiText.ControllerCommandFailed, lastFailure)));
            entries.Add(Separator());
        }

        if (status == null)
        {
            AddWithoutMaster(entries, registeredNodes);
            return new ControllerTrayView(ControllerTrayIcon.MasterMissing, UiTexts.Get(UiText.ControllerTooltipMasterMissing), entries);
        }
        AddSwitch(entries, status.Bot);
        AddNodes(entries, status);
        AddClients(entries, status);
        AddFooter(entries);
        return new ControllerTrayView(IconOf(status), TooltipOf(status), entries);
    }

    private static ControllerTrayIcon IconOf(ControllerStatus status)
    {
        if (status.Bot == BotSwitch.Off)
        {
            return ControllerTrayIcon.Off;
        }
        bool working = status.Clients.Any(client => client.State == ControllerClientState.Active);
        return working ? ControllerTrayIcon.Working : ControllerTrayIcon.On;
    }

    private static string TooltipOf(ControllerStatus status)
    {
        string switchWord = UiTexts.Get(status.Bot == BotSwitch.On ? UiText.ControllerWordOn : UiText.ControllerWordOff);
        int connected = status.Nodes.Count(node => node.Connected);
        int nodes = status.Nodes.Count + status.MoreNodes;
        int clients = status.Clients.Count + status.MoreClients;

        return UiTexts.Format(UiText.ControllerTooltip, switchWord, connected, nodes, clients);
    }

    private static void AddWithoutMaster(List<ControllerTrayEntry> entries, IReadOnlyList<string> registeredNodes)
    {
        entries.Add(Information(UiTexts.Get(UiText.ControllerMasterNotRunning)));
        entries.Add(Separator());
        entries.Add(Heading(UiTexts.Get(UiText.ControllerNodesHeading)));
        foreach (string node in registeredNodes)
        {
            entries.Add(Information(UiTexts.Format(UiText.ControllerNodeUnknown, node)));
        }
        if (registeredNodes.Count == 0)
        {
            entries.Add(Information(UiTexts.Get(UiText.ControllerNoNodes)));
        }
        entries.Add(Separator());
        entries.Add(CommandLine(UiText.ControllerCloseTray, new ControllerTrayCommand(ControllerTrayAction.CloseTray, ""), false));
    }

    private static void AddSwitch(List<ControllerTrayEntry> entries, BotSwitch bot)
    {
        bool on = bot == BotSwitch.On;
        entries.Add(Information(UiTexts.Get(on ? UiText.ControllerBotOn : UiText.ControllerBotOff)));
        ControllerTrayCommand toggle = new(on ? ControllerTrayAction.SwitchBotOff : ControllerTrayAction.SwitchBotOn, "");

        entries.Add(CommandLine(on ? UiText.ControllerSwitchOff : UiText.ControllerSwitchOn, toggle, false));
        entries.Add(Separator());
    }

    private static void AddNodes(List<ControllerTrayEntry> entries, ControllerStatus status)
    {
        entries.Add(Heading(UiTexts.Get(UiText.ControllerNodesHeading)));
        foreach (ControllerNodeRow node in status.Nodes)
        {
            entries.Add(Information(NodeText(node)));
            entries.AddRange(NodeVitalsText.Lines(node).Select(Detail));
        }
        if (status.Nodes.Count == 0 && status.MoreNodes == 0)
        {
            entries.Add(Information(UiTexts.Get(UiText.ControllerNoNodes)));
        }
        if (status.MoreNodes > 0)
        {
            entries.Add(Information(UiTexts.Format(UiText.ControllerMoreEntries, status.MoreNodes)));
        }
        entries.Add(Separator());
    }

    private static string NodeText(ControllerNodeRow node)
    {
        if (!node.Connected)
        {
            return UiTexts.Format(UiText.ControllerNodeOffline, node.Name);
        }
        return node.Version.Length == 0
            ? UiTexts.Format(UiText.ControllerNodeConnected, node.Name)
            : UiTexts.Format(UiText.ControllerNodeConnectedVersion, node.Name, node.Version);
    }

    private static void AddClients(List<ControllerTrayEntry> entries, ControllerStatus status)
    {
        entries.Add(Heading(UiTexts.Get(UiText.ControllerClientsHeading)));
        foreach (ControllerClientRow client in status.Clients)
        {
            AddClient(entries, client);
        }
        if (status.Clients.Count == 0 && status.MoreClients == 0)
        {
            entries.Add(Information(UiTexts.Get(UiText.ControllerNoClients)));
        }
        if (status.MoreClients > 0)
        {
            entries.Add(Information(UiTexts.Format(UiText.ControllerMoreEntries, status.MoreClients)));
        }
        entries.Add(Separator());
    }

    private static void AddClient(List<ControllerTrayEntry> entries, ControllerClientRow client)
    {
        string name = client.Client.Length > 0 ? client.Client : UiTexts.Get(UiText.UnnamedClient);
        string purpose = client.Purpose.Length > 0 ? client.Purpose : UiTexts.Get(UiText.PurposeNotStated);
        entries.Add(Information(UiTexts.Format(ClientStateText(client.State), name)));
        entries.Add(Detail(UiTexts.Format(UiText.ControllerClientTask, purpose)));
        if (client.Nodes.Count > 0)
        {
            entries.Add(Detail(UiTexts.Format(UiText.ControllerClientNodes, string.Join(", ", client.Nodes))));
        }

        bool paused = client.State == ControllerClientState.Paused;
        ControllerTrayAction action = paused ? ControllerTrayAction.ResumeClient : ControllerTrayAction.PauseClient;
        var command = new ControllerTrayCommand(action, client.Instance);
        entries.Add(CommandLine(paused ? UiText.ControllerResumeClient : UiText.ControllerPauseClient, command, true));
    }

    private static UiText ClientStateText(ControllerClientState state)
    {
        return state switch
        {
            ControllerClientState.Active => UiText.ControllerClientActive,
            ControllerClientState.Idle => UiText.ControllerClientIdle,
            ControllerClientState.Paused => UiText.ControllerClientPaused,
            _ => throw new UnreachableException()
        };
    }

    private static void AddFooter(List<ControllerTrayEntry> entries)
    {
        entries.Add(Information(UiTexts.Get(UiText.ControllerNamesAsReported)));
        entries.Add(CommandLine(UiText.ControllerCloseTray, new ControllerTrayCommand(ControllerTrayAction.CloseTray, ""), false));
    }

    private static ControllerTrayEntry Heading(string text)
    {
        return new ControllerTrayEntry(ControllerTrayEntryKind.Heading, text, null, false);
    }

    private static ControllerTrayEntry Information(string text)
    {
        return new ControllerTrayEntry(ControllerTrayEntryKind.Information, text, null, false);
    }

    private static ControllerTrayEntry Detail(string text)
    {
        return new ControllerTrayEntry(ControllerTrayEntryKind.Information, text, null, true);
    }

    private static ControllerTrayEntry CommandLine(UiText text, ControllerTrayCommand command, bool indented)
    {
        return new ControllerTrayEntry(ControllerTrayEntryKind.Command, UiTexts.Get(text), command, indented);
    }

    private static ControllerTrayEntry Separator()
    {
        return new ControllerTrayEntry(ControllerTrayEntryKind.Separator, "", null, false);
    }
}
