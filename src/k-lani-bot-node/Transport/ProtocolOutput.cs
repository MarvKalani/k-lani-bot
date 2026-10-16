using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KLaniBot.Transport;

// The stdio worker keeps standard output for protocol frames only; anything a library prints goes to standard error instead.
public static class ProtocolOutput
{
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int fd, int operation, int argument);
    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)] private static extern int Dup2(int source, int destination);
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)] private static extern int Dup(int source);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int Ioctl(int fd, nuint command);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int handle, IntPtr value);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint access,
        bool inherit,
        uint options);
    // Throws IOException when the standard output handle cannot be duplicated.
    public static StreamWriter Isolate()
    {
        SafeFileHandle output;
        if (OperatingSystem.IsWindows())
        {
            var process = GetCurrentProcess();
            if (!DuplicateHandle(process, GetStdHandle(-11), process, out var copied, 0, false, 2) || !SetStdHandle(-11, GetStdHandle(-12)))
                throw new IOException("Cannot isolate actuator protocol handle.");
            output = new SafeFileHandle(copied, true);
        }
        else
        {
            // Darwin arm64 fcntl is variadic: its third argument uses a different
            // ABI from a fixed P/Invoke signature. Use argument-free FIOCLEX on
            // the duplicated fd before starting any subprocesses instead.
            int copied = OperatingSystem.IsMacOS() ? Dup(1) : Fcntl(1, 1030, 3);
            if (copied < 0)
                throw new IOException("Cannot duplicate actuator protocol descriptor.");
            output = new SafeFileHandle((IntPtr)copied, true);
            if (OperatingSystem.IsMacOS() && Ioctl(copied, 0x20006601) != 0 || Dup2(2, 1) < 0)
            {
                output.Dispose();
                throw new IOException("Cannot isolate actuator protocol descriptor.");
            }
        }
        Console.SetOut(Console.Error);
        return new StreamWriter(new FileStream(output, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
    }
}
