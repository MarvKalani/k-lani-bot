namespace KLaniBot.Startup;

// Every way the node executable can be started. Exactly one is chosen per process; the option that
// selects it is listed in CommandLine.
public enum NodeCommand
{
    Enroll,
    ApplyUpdate,
    InstallCartridge,
    RequestApplePermissions,
    ShowApplePermissions,
    ScanApps,
    CreateInventory,
    Evaluate,
    MasterAdmin,
    ControllerTray,
    PairingMaster,
    Connect,
    Serve,
    Call,
    Init,
    ExportSchemas,
    Mcp,
    StdioWorker,
    LocalHttp,
    VerifyAudit,
    CreateBackupKey
}
