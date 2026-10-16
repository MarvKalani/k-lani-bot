using System.Diagnostics;

namespace KLaniBot.Startup;

// Chooses the one command a process runs. Two selecting options at once used to run whichever was
// tested first; now that is refused, so a wrong service definition fails at start instead of quietly
// doing something else. Required options are checked here too, before a command opens anything.
public static class CommandLine
{
    // An option that selects a command, and the command it selects.
    private sealed record CommandSelector(string Option, NodeCommand Command);

    private static readonly CommandSelector[] Selectors =
    [
        new("--enroll", NodeCommand.Enroll),
        new("--apply-update", NodeCommand.ApplyUpdate),
        new("--cartridge", NodeCommand.InstallCartridge),
        new("--request-apple-permissions", NodeCommand.RequestApplePermissions),
        new("--apple-permissions", NodeCommand.ShowApplePermissions),
        new("--scan-apps", NodeCommand.ScanApps),
        new("--inventory", NodeCommand.CreateInventory),
        new("--evaluate", NodeCommand.Evaluate),
        new("--master-admin", NodeCommand.MasterAdmin),
        new("--pairing-master", NodeCommand.PairingMaster),
        new("--connect", NodeCommand.Connect),
        new("--serve", NodeCommand.Serve),
        new("--call", NodeCommand.Call),
        new("--init", NodeCommand.Init),
        new("--export-schemas", NodeCommand.ExportSchemas),
        new("--mcp", NodeCommand.Mcp),
        new("--stdio-worker", NodeCommand.StdioWorker),
        new("--local-http", NodeCommand.LocalHttp),
        new("--verify-audit", NodeCommand.VerifyAudit),
        new("--create-backup-key", NodeCommand.CreateBackupKey)
    ];

    // Throws ArgumentException when no or several commands are selected, or a required option is missing.
    public static NodeCommand Select(NodeArguments arguments)
    {
        var selecting = Selectors.Where(selector => arguments.Has(selector.Option)).ToList();
        if (selecting.Count == 0)
        {
            throw new ArgumentException(
                "No command given. Use --init to issue credentials, --serve or --connect for the transport supervisor, "
                + "--stdio-worker, or --local-http for loopback diagnostics.");
        }
        if (selecting.Count > 1)
        {
            string names = string.Join(" and ", selecting.Select(selector => selector.Option));
            throw new ArgumentException("Options " + names + " select different commands; use exactly one.");
        }
        NodeCommand command = selecting[0].Command;
        foreach (string option in RequiredOptions(command))
        {
            arguments.RequireOption(option);
        }
        if (command != NodeCommand.Mcp
            && (arguments.Has("--ui-profile") || arguments.Has("--learning-root") || arguments.Has("--ui-config")))
        {
            throw new ArgumentException("UI profiles, learning roots and UI configuration require --mcp.");
        }
        return command;
    }

    // Options without which a command cannot start. Options with a default, like --port, are not here.
    public static IReadOnlyList<string> RequiredOptions(NodeCommand command)
    {
        return command switch
        {
            NodeCommand.Enroll => ["--enroll"],
            NodeCommand.ApplyUpdate => ["--apply-update"],
            NodeCommand.InstallCartridge => ["--cartridge"],
            NodeCommand.CreateInventory => ["--inventory"],
            NodeCommand.Evaluate => ["--evaluate", "--nodes"],
            NodeCommand.MasterAdmin => ["--state"],
            NodeCommand.PairingMaster => ["--state", "--bind"],
            NodeCommand.Connect => ["--config"],
            NodeCommand.Serve => ["--config"],
            NodeCommand.Call => ["--node", "--nodes"],
            NodeCommand.ExportSchemas => ["--export-schemas"],
            NodeCommand.Mcp => ["--nodes"],
            NodeCommand.StdioWorker => ["--policy"],
            NodeCommand.LocalHttp => ["--policy"],
            NodeCommand.VerifyAudit => ["--verify-audit"],
            NodeCommand.CreateBackupKey => ["--policy"],
            NodeCommand.RequestApplePermissions or NodeCommand.ShowApplePermissions or NodeCommand.ScanApps or NodeCommand.Init => [],
            _ => throw new UnreachableException()
        };
    }
}
