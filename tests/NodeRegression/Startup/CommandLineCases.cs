using KLaniBot;
using KLaniBot.Startup;

// The node used to run whichever command it tested first, and a mistyped port ended in the raw .NET
// parse message. These cases pin the choice of the command and the wording of argument errors.
internal static class CommandLineCases
{
    // Arguments and the one command the command line must select for them.
    private sealed record Selection(string[] Arguments, NodeCommand Command);

    // Arguments the command line must refuse, and a part of the message that has to say why.
    private sealed record Refusal(string[] Arguments, string ExpectedPart);

    private static readonly Selection[] Selections =
    [
        new(["--enroll", "invitation.json"], NodeCommand.Enroll),
        new(["--apply-update", "plan.json"], NodeCommand.ApplyUpdate),
        new(["--cartridge", "cartridge.json", "--cartridge-action", "install"], NodeCommand.InstallCartridge),
        new(["--request-apple-permissions", "--permission-report", "report.json"], NodeCommand.RequestApplePermissions),
        new(["--apple-permissions"], NodeCommand.ShowApplePermissions),
        new(["--scan-apps", "--inventory-file", "inventory.json"], NodeCommand.ScanApps),
        new(["--inventory", "release", "--signing-key", "key"], NodeCommand.CreateInventory),
        new(["--evaluate", "spec.json", "--nodes", "nodes.json"], NodeCommand.Evaluate),
        new(["--master-admin", "--state", "state"], NodeCommand.MasterAdmin),
        new(["--controller-tray", "--state", "state"], NodeCommand.ControllerTray),
        new(["--pairing-master", "--state", "state", "--bind", "127.0.0.1"], NodeCommand.PairingMaster),
        new(["--connect", "--config", "node.json"], NodeCommand.Connect),
        new(["--serve", "--config", "node.json"], NodeCommand.Serve),
        new(["--call", "--node", "fixture", "--nodes", "nodes.json"], NodeCommand.Call),
        new(["--init", "--workspace", "workspace", "--state", "state"], NodeCommand.Init),
        new(["--export-schemas", "schemas"], NodeCommand.ExportSchemas),
        new(["--mcp", "--nodes", "nodes.json", "--mcp-full-text"], NodeCommand.Mcp),
        new(["--stdio-worker", "--policy", "policy.json", "--android", "--serial", "device"], NodeCommand.StdioWorker),
        new(["--local-http", "--policy", "policy.json", "--port", "5005"], NodeCommand.LocalHttp),
        new(["--verify-audit", "state/actuator"], NodeCommand.VerifyAudit),
        new(["--create-backup-key", "--policy", "policy.json"], NodeCommand.CreateBackupKey)
    ];

    private static readonly Refusal[] Refusals =
    [
        new([], "No command given"),
        new(["--policy", "policy.json"], "No command given"),
        new(["--serve", "--connect", "--config", "node.json"], "--connect and --serve select different commands"),
        new(["--stdio-worker", "--local-http", "--policy", "policy.json"], "select different commands"),
        new(["--init", "--mcp", "--nodes", "nodes.json"], "select different commands"),
        new(["--serve"], "--config <value> is required."),
        new(["--evaluate", "spec.json"], "--nodes <value> is required."),
        new(["--pairing-master", "--state", "state"], "--bind <value> is required."),
        new(["--call", "--nodes", "nodes.json"], "--node <value> is required."),
        new(["--stdio-worker"], "--policy <value> is required."),
        new(["--export-schemas"], "--export-schemas <value> is required."),
        new(["--create-backup-key"], "--policy <value> is required."),
        new(["--controller-tray"], "--state <value> is required."),
        new(["--controller-tray", "--mcp", "--state", "state", "--nodes", "nodes.json"], "select different commands")
    ];

    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Command line: " + name);
            }
            passed++;
        }
        EveryCommandIsSelectable(Check);
        RefusalsNameTheProblem(Check);
        NumbersAreCheckedWithTheirOptionName(Check);
        FailuresNameTheirType(Check);
        return passed;
    }

    private static void EveryCommandIsSelectable(Action<bool, string> check)
    {
        var covered = Selections.Select(selection => selection.Command).Distinct().Count();
        check(covered == Enum.GetValues<NodeCommand>().Length, "the table selects every command");
        foreach ((string[] arguments, NodeCommand command) in Selections)
        {
            check(CommandLine.Select(new NodeArguments(arguments)) == command, "selects " + command);
        }
    }

    private static void RefusalsNameTheProblem(Action<bool, string> check)
    {
        foreach ((string[] arguments, string expectedPart) in Refusals)
        {
            string message = MessageOf(() => CommandLine.Select(new NodeArguments(arguments)));
            check(message.Contains(expectedPart), "'" + string.Join(" ", arguments) + "' is refused with: " + expectedPart);
        }
    }

    private static void NumbersAreCheckedWithTheirOptionName(Action<bool, string> check)
    {
        check(new NodeArguments(["--port", "6000"]).GetPort("--port", 5005) == 6000, "a given port is used");
        check(new NodeArguments([]).GetPort("--port", 5005) == 5005, "a missing port falls back");
        check(new NodeArguments(["--application", "a", "--application", "b"]).Values("--application").SequenceEqual(["a", "b"]),
            "an option given twice yields both values in order");
        string mistyped = MessageOf(() => new NodeArguments(["--port", "abc"]).GetPort("--port", 5005));
        check(mistyped == "--port must be a whole number.", "a mistyped port names the option");
        string outside = MessageOf(() => new NodeArguments(["--port", "70000"]).GetPort("--port", 5005));
        check(outside == "--port must be a whole number from 1 to 65535.", "a port outside the range names the range");
        string negative = MessageOf(() => new NodeArguments(["--port", "-1"]).GetPort("--port", 5005));
        check(negative == "--port must be a whole number.", "a sign is not accepted");
        // A credential pasted into the wrong place must not be echoed into a collected log.
        string secret = "kl-secret-" + new string('x', 32);
        string misplaced = MessageOf(() => new NodeArguments(["--port", secret]).GetPort("--port", 5005));
        check(!misplaced.Contains(secret), "the rejected value is not repeated");
    }

    private static void FailuresNameTheirType(Action<bool, string> check)
    {
        Exception failure;
        try
        {
            throw new InvalidDataException("Frame broken.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        check(CommandFailure.Describe(failure, false) == "InvalidDataException: Frame broken.", "type and message");
        string withTrace = CommandFailure.Describe(failure, true);
        bool tracePresent = withTrace.StartsWith("InvalidDataException: Frame broken.")
            && withTrace.Contains(nameof(FailuresNameTheirType));
        check(tracePresent, "stack trace on request");
    }

    private static string MessageOf(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
        return "";
    }
}
