using System.Diagnostics;
using System.Globalization;

namespace KLaniBot.Transport.CompactUi;

// Recovery distinguishes a proven dead owner from a live or inaccessible process; unknown liveness never authorizes lock removal.
public enum CompactProcessState
{
    Dead,
    Live,
    Unknown
}

// A process lifetime token prevents PID reuse from being mistaken for the original writer.
public sealed record CompactProcessObservation(CompactProcessState State, string? Started = null);

// Revision locks probe the current OS process identity instead of trusting a timeout or historical process ID.
public static class CompactProcessLifetime
{
    public static CompactProcessObservation Observe(int processId)
    {
        if (processId < 1)
        {
            return new(CompactProcessState.Unknown);
        }
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string text = File.ReadAllText("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/stat");
                string[] fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 19 ? new(CompactProcessState.Live, fields[19]) : new(CompactProcessState.Unknown);
            }
            using Process process = Process.GetProcessById(processId);
            return process.HasExited ? new(CompactProcessState.Dead)
                : new(CompactProcessState.Live, process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (FileNotFoundException)
        {
            return new(CompactProcessState.Dead);
        }
        catch (DirectoryNotFoundException)
        {
            return new(CompactProcessState.Dead);
        }
        catch (ArgumentException)
        {
            return new(CompactProcessState.Dead);
        }
        catch (Exception)
        {
            return new(CompactProcessState.Unknown);
        }
    }
}
