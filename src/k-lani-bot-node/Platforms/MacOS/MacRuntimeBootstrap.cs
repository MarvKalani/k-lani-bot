using System.Runtime.InteropServices;

namespace KLaniBot.Platforms.MacOS;

// A single-file node must apply CLR startup knobs before creating pipe readers.
// execv preserves PID, stdin/stdout, arguments and the app's TCC identity; it
// does not fork a child with the affected async pipe/R2R combination.
internal static class MacRuntimeBootstrap
{
    private static readonly Dictionary<string, string> RestartEnvironment = new()
    {
        ["COMPlus_ReadyToRun"] = "0",
        ["DOTNET_ReadyToRun"] = "0",
        ["K_LANI_BOT_MAC_RUNTIME_RESTARTED"] = "1"
    };

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int ExecV([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr arguments);
    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnv(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        int overwrite);
    // Throws IOException when the restart with the startup settings fails or did not keep them.
    public static void EnsureCompatibleRuntime(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return;
        if (Environment.GetEnvironmentVariable("COMPlus_ReadyToRun") == "0"
            && Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") == "0")
            return;
        if (Environment.GetEnvironmentVariable("K_LANI_BOT_MAC_RUNTIME_RESTARTED") == "1")
            throw new IOException("macOS CLR startup settings did not persist; node did not start.");
        string executable = Environment.ProcessPath ?? throw new IOException("macOS executable path unavailable.");
        var arguments = new List<string> { executable };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        arguments.AddRange(args);
        // .NET caches its Unix environment; changing only the managed copy does
        // not update the native environment consumed by execv / CLR startup.
        foreach (KeyValuePair<string, string> setting in RestartEnvironment)
            if (SetEnv(setting.Key, setting.Value, 1) != 0)
                throw new IOException("Cannot configure macOS runtime startup.");
        var strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        IntPtr pointers = Marshal.AllocHGlobal(checked((strings.Length + 1) * IntPtr.Size));
        try
        {
            for (int index = 0; index < strings.Length; index++)
                Marshal.WriteIntPtr(pointers, index * IntPtr.Size, strings[index]);
            Marshal.WriteIntPtr(pointers, strings.Length * IntPtr.Size, IntPtr.Zero);
            ExecV(executable, pointers);
            throw new IOException($"macOS runtime restart failed (errno {Marshal.GetLastPInvokeError()}); node did not start.");
        }
        finally { Marshal.FreeHGlobal(pointers); foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer); }
    }
}
