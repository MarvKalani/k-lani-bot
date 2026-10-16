using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Localization;
using KLaniBot.Platforms;
using KLaniBot.Platforms.Linux;
using KLaniBot.Platforms.MacOS;
using KLaniBot.Platforms.Mobile;
using KLaniBot.Platforms.Windows;
using KLaniBot.Transport;
using KLaniBot.Visibility;

namespace KLaniBot.Startup;

// The two ways the process that touches the desktop is started. Both run inside the person's desktop
// session, under the visible indicator that Program.Main created for them.
internal static class ActuatorCommands
{
    // The supervisor (--serve or --connect) starts this as a child process and speaks the line protocol
    // of StdioWorkerLoop with it.
    // Throws InvalidOperationException without a visible desktop host.
    public static async Task<int> StdioWorkerAsync(
        NodeArguments arguments,
        AccessVisibility? visibility,
        NativeVisibilityHost? nativeHost)
    {
        if (visibility == null)
        {
            throw new InvalidOperationException("A visible desktop host is mandatory.");
        }
        string policyPath = arguments.RequireOption("--policy");
        NodePolicy policy = NodePolicy.Load(policyPath);
        UiTexts.Use(policy.UiLanguage);
        // The protocol descriptor is taken away from stdout before any backend exists: a helper process
        // started by a backend inherits stdout and must not be able to write protocol frames.
        using StreamWriter protocol = ProtocolOutput.Isolate();
        IUiService ui = CreateUiService(arguments, nativeHost);
        using var uiLifetime = ui as IDisposable;
        using var actor = new DesktopActor(new ActionDispatcher(ui, policy, visibility));
        using var cancellation = ConsoleCancellation.Create();
        var loop = new StdioWorkerLoop(policy, policyPath, visibility, actor.ExecuteAsync);
        await loop.RunAsync(Console.OpenStandardInput(), protocol, cancellation.Token);
        return 0;
    }

    // A developer starts this on their own machine to try tools from a browser or curl on loopback.
    public static async Task<int> LocalHttpAsync(
        NodeArguments arguments,
        AccessVisibility? visibility,
        NativeVisibilityHost? nativeHost)
    {
        NodePolicy policy = NodePolicy.Load(arguments.RequireOption("--policy"));
        UiTexts.Use(policy.UiLanguage);
        int port = arguments.GetPort("--port", 5005);
        IUiService ui = CreateUiService(arguments, nativeHost);
        using var uiLifetime = ui as IDisposable;
        // Like the stdio worker, the loopback server keeps stdout free of diagnostics.
        Console.SetOut(Console.Error);
        using var actor = new DesktopActor(new ActionDispatcher(ui, policy, visibility));
        using var cancellation = ConsoleCancellation.Create();
        await new HttpMcpServer(actor, policy, port).StartAsync(cancellation.Token);
        return 0;
    }

    // Mobile targets are chosen explicitly; without such an option the node operates its own desktop.
    private static IUiService CreateUiService(NodeArguments arguments, NativeVisibilityHost? nativeHost)
    {
        if (arguments.Option("--ios-bridge") is { } iosBridge)
        {
            return new IosSimulatorUiService(iosBridge, arguments.RequireOption("--ios-bundle"));
        }
        if (arguments.Has("--android"))
        {
            return new AdbUiService(arguments.Option("--adb") ?? "adb", arguments.RequireOption("--serial"));
        }
        return CreateDesktopUiService(nativeHost);
    }

    private static IUiService CreateDesktopUiService(NativeVisibilityHost? nativeHost)
    {
#if WINDOWS
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        return new UiAutomationService();
#else
        if (OperatingSystem.IsMacOS())
        {
            return new MacUiService();
        }
        if (nativeHost != null)
        {
            // AT-SPI objects must be created and used on the thread that owns the indicator's main loop.
            return new NativeThreadUiService(nativeHost.NativeCall(() => new LinuxUiService()), nativeHost);
        }
        return new LinuxUiService();
#endif
    }
}
