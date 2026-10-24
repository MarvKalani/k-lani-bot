using System.Globalization;
using System.Runtime.InteropServices;

namespace KLaniBot.Fleet;

// Measures this computer's vital signs when the master asks. Each value is read on its own and becomes null when it cannot be
// read, so one missing source never costs the others. Nothing here waits: the master's relay drops a node whose answer is
// late, so CPU load is the average since the previous measurement instead of a fresh sample, and the first answer has none.
// Uptime is time since the system started, sleep included, because a sleeping computer is still the same running system.
internal sealed class NodeVitalsProbe
{
    private readonly object _gate = new();
    private CpuTicks? _previous;

    // Busy and total processor ticks since the system started, summed over all processors.
    internal readonly record struct CpuTicks(ulong Busy, ulong Total);

    // Available and total physical memory in bytes; either is null when the system does not report it.
    internal readonly record struct MemoryBytes(long? Available, long? Total);

    public NodeVitals Measure()
    {
        double? load = null;
        lock (_gate)
        {
            CpuTicks? current = Read(ReadCpuTicks);
            if (_previous is { } before && current is { } after)
            {
                load = LoadPercent(before, after);
            }
            _previous = current ?? _previous;
        }

        MemoryBytes memory = Read(ReadMemory) ?? new MemoryBytes(null, null);
        string volume = SystemVolume();
        DriveInfo? drive = Read(() => new DriveInfo(volume));
        return new NodeVitals(
            Read(SystemName) ?? RuntimeInformation.OSDescription,
            memory.Available,
            memory.Total,
            volume,
            Read(() => drive?.AvailableFreeSpace),
            Read(() => drive?.TotalSize),
            load,
            Read(ReadUptimeSeconds));
    }

    public static double? LoadPercent(CpuTicks before, CpuTicks after)
    {
        if (after.Total <= before.Total || after.Busy < before.Busy)
        {
            return null;
        }
        double busy = after.Busy - before.Busy;
        return Math.Round(Math.Min(100.0, 100.0 * busy / (after.Total - before.Total)), 1);
    }

    public static MemoryBytes LinuxMemory(string meminfo)
    {
        return new MemoryBytes(MeminfoBytes(meminfo, "MemAvailable:"), MeminfoBytes(meminfo, "MemTotal:"));
    }

    // The first line of /proc/stat: user nice system idle iowait irq softirq steal, then guest times already counted in user.
    public static CpuTicks? LinuxCpuTicks(string stat)
    {
        string? line = stat.Split('\n').FirstOrDefault(entry => entry.StartsWith("cpu ", StringComparison.Ordinal));
        string[] fields = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (fields.Length < 9)
        {
            return null;
        }
        ulong[] ticks = fields[1..9].Select(field => ulong.Parse(field, CultureInfo.InvariantCulture)).ToArray();
        ulong total = ticks.Aggregate(0UL, (sum, tick) => sum + tick);
        return new CpuTicks(total - ticks[3] - ticks[4], total);
    }

    public static string? LinuxSystemName(string osRelease)
    {
        string? line = osRelease.Split('\n').FirstOrDefault(entry => entry.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
        string? name = line?["PRETTY_NAME=".Length..].Trim().Trim('"');
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public static long LinuxUptimeSeconds(string uptime)
    {
        string seconds = uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return (long)double.Parse(seconds, CultureInfo.InvariantCulture);
    }

    // Windows 11 still reports version 10.0; only its build number, 22000 and later, tells it apart.
    public static string WindowsSystemName(Version version)
    {
        string name = version.Major == 10 ? (version.Build >= 22000 ? "Windows 11" : "Windows 10") : "Windows " + version.ToString(2);
        return name + " (Build " + version.Build.ToString(CultureInfo.InvariantCulture) + ")";
    }

    // A source that is missing, unreadable or malformed on this system leaves its value unknown.
    private static T? Read<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException
            or OverflowException or ArgumentException or IndexOutOfRangeException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            return default;
        }
    }

    private static long? MeminfoBytes(string meminfo, string key)
    {
        string? line = meminfo.Split('\n').FirstOrDefault(entry => entry.StartsWith(key, StringComparison.Ordinal));
        string[] fields = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return fields.Length >= 2 ? long.Parse(fields[1], CultureInfo.InvariantCulture) * 1024 : null;
    }

    private static string SystemVolume()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        }
        // The macOS root volume is sealed and read-only; programs and data live on the data volume.
        return OperatingSystem.IsMacOS() && Directory.Exists("/System/Volumes/Data") ? "/System/Volumes/Data" : "/";
    }

    private static string? SystemName()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSystemName(Environment.OSVersion.Version);
        }
        if (OperatingSystem.IsMacOS())
        {
            Version version = Environment.OSVersion.Version;
            return "macOS " + version.ToString(version.Build > 0 ? 3 : 2);
        }
        return LinuxSystemName(File.ReadAllText("/etc/os-release"));
    }

    private static CpuTicks? ReadCpuTicks()
    {
        if (OperatingSystem.IsWindows())
        {
            return Windows.CpuTicks();
        }
        return OperatingSystem.IsMacOS() ? Mac.CpuTicks() : LinuxCpuTicks(File.ReadAllText("/proc/stat"));
    }

    private static MemoryBytes? ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Windows.Memory();
        }
        return OperatingSystem.IsMacOS() ? Mac.Memory() : LinuxMemory(File.ReadAllText("/proc/meminfo"));
    }

    private static long? ReadUptimeSeconds()
    {
        if (OperatingSystem.IsWindows())
        {
            return Windows.UptimeSeconds();
        }
        return OperatingSystem.IsMacOS() ? Mac.UptimeSeconds() : LinuxUptimeSeconds(File.ReadAllText("/proc/uptime"));
    }

    // Kernel32 calls; GetTickCount64 counts sleep, GetSystemTimes' kernel time includes idle time.
    private static class Windows
    {
        // MEMORYSTATUSEX as GlobalMemoryStatusEx fills it; Length must be set before the call.
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public static MemoryBytes? Memory()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? new MemoryBytes((long)status.AvailablePhysical, (long)status.TotalPhysical) : null;
        }

        public static CpuTicks? CpuTicks()
        {
            return GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)
                ? new CpuTicks(kernel + user - idle, kernel + user)
                : null;
        }

        public static long UptimeSeconds()
        {
            return (long)(GetTickCount64() / 1000);
        }
    }

    // libSystem calls. kern.memorystatus_level is the share of memory the system counts as available, as Activity Monitor
    // shows it; kern.boottime gives the start of the system, so sleep is included.
    private static class Mac
    {
        private const int HostCpuLoadInfo = 3;
        // Each mach_host_self call adds a send right; asking once keeps the count from growing with every poll.
        private static readonly Lazy<uint> Host = new(MachHostSelf);

        [DllImport("libc", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, out long value, ref nint size, nint newValue, nint newSize);

        [DllImport("libc", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, out int value, ref nint size, nint newValue, nint newSize);

        [DllImport("libc", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, [Out] long[] value, ref nint size, nint newValue, nint newSize);

        [DllImport("libc", EntryPoint = "mach_host_self")]
        private static extern uint MachHostSelf();

        [DllImport("libc", EntryPoint = "host_statistics")]
        private static extern int HostStatistics(uint host, int flavor, [Out] uint[] info, ref uint count);

        public static MemoryBytes? Memory()
        {
            nint longSize = sizeof(long);
            nint intSize = sizeof(int);
            if (SysctlByName("hw.memsize", out long total, ref longSize, 0, 0) != 0
                || SysctlByName("kern.memorystatus_level", out int level, ref intSize, 0, 0) != 0 || level is < 0 or > 100)
            {
                return null;
            }
            return new MemoryBytes(total / 100 * level, total);
        }

        // user, system, idle and nice ticks of all processors.
        public static CpuTicks? CpuTicks()
        {
            uint[] ticks = new uint[4];
            uint count = 4;
            if (HostStatistics(Host.Value, HostCpuLoadInfo, ticks, ref count) != 0)
            {
                return null;
            }
            ulong busy = (ulong)ticks[0] + ticks[1] + ticks[3];
            return new CpuTicks(busy, busy + ticks[2]);
        }

        // struct timeval: seconds and microseconds, each 8 bytes on 64-bit macOS.
        public static long? UptimeSeconds()
        {
            long[] boot = new long[2];
            nint size = 2 * sizeof(long);
            return SysctlByName("kern.boottime", boot, ref size, 0, 0) == 0 && boot[0] > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - boot[0]
                : null;
        }
    }
}
