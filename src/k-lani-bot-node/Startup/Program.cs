using System.Diagnostics;
using System.Text;
using KLaniBot.Audit;
using KLaniBot.Common;
using KLaniBot.Fleet;
using KLaniBot.Guard;
using KLaniBot.Localization;
using KLaniBot.Platforms.MacOS;
using KLaniBot.Platforms.Windows;
using KLaniBot.Transport;
using KLaniBot.Updates;
using KLaniBot.Visibility;

namespace KLaniBot.Startup;

// Every way the node executable is started enters here; the command line selects exactly one command.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            MacRuntimeBootstrap.EnsureCompatibleRuntime(args);
#if WINDOWS
            int serviceCheckIndex = Array.IndexOf(args, "--service-check");
            if (serviceCheckIndex >= 0 && serviceCheckIndex + 1 < args.Length)
            {
                _ = ExecutionRoleSelector.FromArguments(args);
                return WindowsServiceCheck.Run(args[serviceCheckIndex + 1]);
            }
            if (args.Length == 0)
            {
                return WindowsNodeSetup.Run();
            }
#endif
            if (ExecutionRoleSelector.FromArguments(args) == ExecutionRole.NodeWorker)
            {
                return RunVisibleWorker(args);
            }
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            WorkerStartupFailure.Record(args, exception);
            Console.Error.WriteLine(CommandFailure.Describe(exception));
            return 1;
        }
    }

    // Only the process that acts on the desktop owns the indicator and the activity border, so the person
    // at the machine can always see when it runs. The native host needs the main thread for its event
    // loop; the command runs beside it.
    private static int RunVisibleWorker(string[] args)
    {
        NodePolicy policy = NodePolicy.Load(new NodeArguments(args).RequireOption("--policy"));
        UiTexts.Use(policy.UiLanguage);
#if WINDOWS
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
#endif
        using var display = NativeVisibilityHost.Create();
        display.SetNodeIdentity(policy.NodeId);
        display.SetNodeStateDirectory(policy.StateRoot);
        string auditRoot = Path.Combine(policy.StateRoot, "visibility");
        PrivateFile.CreateDirectory(auditRoot);
        var journal = new AuditJournal(auditRoot);
        using var visible = new AccessVisibility(
            AccessVisibility.LoadOptions(policy),
            display,
            journal.Append,
            save: options => AccessVisibility.SaveOptions(policy, options));
        display.Attach(visible);
        return display.Run(() => RunAsync(args, visible, display));
    }

    private static async Task<int> RunAsync(string[] args, AccessVisibility? visible = null, NativeVisibilityHost? nativeHost = null)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var arguments = new NodeArguments(args);
        try
        {
            NodeCommand command = CommandLine.Select(arguments);
            return await RunCommandAsync(command, arguments, visible, nativeHost);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(CommandFailure.Describe(exception));
#if WINDOWS
            // The installer starts the reverse connection without a console window (CreateNoWindow), so
            // a dialog is the only place where the person would see why it stopped.
            if (arguments.Has("--connect"))
            {
                System.Windows.MessageBox.Show(exception.Message, ProductIdentity.ProductName,
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
#endif
            return 1;
        }
    }

    private static Task<int> RunCommandAsync(
        NodeCommand command,
        NodeArguments arguments,
        AccessVisibility? visible,
        NativeVisibilityHost? nativeHost)
    {
        return command switch
        {
            NodeCommand.Enroll => SetupCommands.EnrollAsync(arguments),
            NodeCommand.ApplyUpdate => SetupCommands.ApplyUpdateAsync(arguments),
            NodeCommand.InstallCartridge => SetupCommands.InstallCartridgeAsync(arguments),
            NodeCommand.Init => SetupCommands.InitAsync(arguments),
            NodeCommand.ScanApps => Task.FromResult(SetupCommands.ScanApps(arguments)),
            NodeCommand.RequestApplePermissions => Task.FromResult(ApplePermissionCommands.Request(arguments)),
            NodeCommand.ShowApplePermissions => Task.FromResult(ApplePermissionCommands.Show()),
            NodeCommand.CreateInventory => Task.FromResult(ReleaseCommands.CreateInventory(arguments)),
            NodeCommand.Evaluate => ReleaseCommands.EvaluateAsync(arguments),
            NodeCommand.ExportSchemas => Task.FromResult(ReleaseCommands.ExportSchemas(arguments)),
            NodeCommand.MasterAdmin => FleetCommands.MasterAdminAsync(arguments),
            NodeCommand.PairingMaster => FleetCommands.PairingMasterAsync(arguments),
            NodeCommand.Connect => FleetCommands.ConnectAsync(arguments),
            NodeCommand.Serve => FleetCommands.ServeAsync(arguments),
            NodeCommand.Call => FleetCommands.CallAsync(arguments),
            NodeCommand.Mcp => FleetCommands.McpAsync(arguments),
            NodeCommand.StdioWorker => ActuatorCommands.StdioWorkerAsync(arguments, visible, nativeHost),
            NodeCommand.LocalHttp => ActuatorCommands.LocalHttpAsync(arguments, visible, nativeHost),
            NodeCommand.VerifyAudit => Task.FromResult(AuditCommands.Verify(arguments)),
            NodeCommand.CreateBackupKey => Task.FromResult(BackupKeyCommands.Create(arguments)),
            _ => throw new UnreachableException()
        };
    }
}
