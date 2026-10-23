using KLaniBot.ControllerTray;
using KLaniBot.Fleet;
using KLaniBot.Localization;

// The controller tray's menu, computed without a desktop: the bot switch, every node with its state, one pause or resume line
// per MCP client sending the right administration command, and the nodes from the registry while no master answers.
internal static class ControllerTrayMenuCases
{
    public static int Run()
    {
        var checks = new Checks("Controller tray menu");
        UiLanguage previous = UiTexts.Language;
        UiTexts.Use(UiLanguage.German);
        try
        {
            MenuShowsNodesAndClients(checks);
            PausedClientOffersResume(checks);
            MissingMasterShowsRegistry(checks);
            FailureIsShownFirst(checks);
        }
        finally
        {
            UiTexts.Use(previous);
        }
        return checks.Passed;
    }

    private static ControllerStatus Status(BotSwitch bot, params ControllerClientRow[] clients)
    {
        ControllerNodeRow[] nodes = [new("office-pc", true, "0.2610.0604"), new("laptop", false, "")];
        return new ControllerStatus(bot, nodes, 0, clients, 0);
    }

    private static ControllerClientRow Client(string instance, string name, ControllerClientState state)
    {
        return new ControllerClientRow(instance, name, "", ["office-pc"], state, 3);
    }

    private static List<string> Texts(ControllerTrayView view)
    {
        return view.Entries.Select(entry => entry.Text).ToList();
    }

    private static void MenuShowsNodesAndClients(Checks checks)
    {
        ControllerTrayView view = ControllerTrayMenu.Build(
            Status(BotSwitch.On, Client("a", "Claude Code", ControllerClientState.Active), Client("b", "", ControllerClientState.Idle)),
            [],
            "");
        List<string> texts = Texts(view);

        checks.That(texts.Contains("k-lani-bot ist an: KI-Clients dürfen arbeiten"), "the switch state is said in plain German");
        checks.That(texts.Contains("● office-pc: verbunden, Version 0.2610.0604") && texts.Contains("○ laptop: nicht verbunden"),
            "every node shows whether it is connected");
        checks.That(texts.Contains("Claude Code: arbeitet gerade") && texts.Contains("Nicht benannter Client: verbunden, wartet"),
            "every client shows its reported name and state");
        checks.That(texts.Count(text => text == "Aufgabe: Auftrag nicht angegeben") == 2 && texts.Contains("Auf: office-pc"),
            "every client shows its task, or that none was stated, and the nodes it uses");
        var pauses = view.Entries.Where(entry => entry.Command?.Action == ControllerTrayAction.PauseClient).ToList();
        checks.That(pauses.Count == 2 && pauses[0].Command!.MasterCommand() == "client-pause a" && pauses.All(entry => entry.Indented),
            "each client has its own pause line that names its instance");
        ControllerTrayEntry switchOff = view.Entries.Single(entry => entry.Command?.Action == ControllerTrayAction.SwitchBotOff);
        checks.That(switchOff.Command!.MasterCommand() == "bot off", "the switch line turns the bot off");
        checks.That(view.Icon == ControllerTrayIcon.Working, "the icon shows that a client is working");
        checks.That(view.Tooltip == "k-lani-bot an · 1 von 2 Computern verbunden · 2 KI-Clients", "the hover text sums up the state");
        ControllerTrayView same = ControllerTrayMenu.Build(
            Status(BotSwitch.On, Client("a", "Claude Code", ControllerClientState.Active), Client("b", "", ControllerClientState.Idle)),
            [],
            "");
        checks.That(same.Stamp() == view.Stamp(), "an unchanged status does not redraw the menu");
    }

    private static void PausedClientOffersResume(Checks checks)
    {
        ControllerStatus status = Status(BotSwitch.Off, Client("a", "codex", ControllerClientState.Paused));
        ControllerTrayView view = ControllerTrayMenu.Build(status, [], "");
        List<string> texts = Texts(view);

        checks.That(texts.Contains("codex: pausiert"), "a paused client says so");
        ControllerTrayEntry resume = view.Entries.Single(entry => entry.Command?.Action == ControllerTrayAction.ResumeClient);
        checks.That(resume.Command!.MasterCommand() == "client-resume a", "a paused client offers resume instead of pause");
        checks.That(view.Entries.Any(entry => entry.Command?.MasterCommand() == "bot on") && view.Icon == ControllerTrayIcon.Off,
            "a switched-off bot offers to switch on and shows the off icon");
    }

    private static void MissingMasterShowsRegistry(Checks checks)
    {
        ControllerTrayView view = ControllerTrayMenu.Build(null, ["office-pc"], "");
        List<string> texts = Texts(view);

        checks.That(texts.Contains("Der Master läuft nicht: keine Verbindung zu den Computern"), "a missing master is said plainly");
        checks.That(texts.Contains("? office-pc: Zustand unbekannt"), "registered nodes are listed with unknown state");
        checks.That(view.Entries.Where(entry => entry.Command != null).All(entry => entry.Command!.MasterCommand() == null),
            "without a master only closing the tray is offered");
        checks.That(view.Icon == ControllerTrayIcon.MasterMissing, "the icon shows the missing master");
    }

    private static void FailureIsShownFirst(Checks checks)
    {
        ControllerTrayView view = ControllerTrayMenu.Build(Status(BotSwitch.On), [], "No MCP client with this instance uses the bot.");

        checks.That(view.Entries[0].Text == "Das hat nicht geklappt: No MCP client with this instance uses the bot.",
            "a refused command is shown at the top");
        checks.That(Texts(view).Contains("Gerade nutzt kein KI-Client den Bot"), "no clients is said plainly");
    }
}
