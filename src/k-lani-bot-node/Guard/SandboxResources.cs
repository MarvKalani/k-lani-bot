using System.Text.Json;

namespace KLaniBot.Guard;

// Limits for an actuator that --init places in a container: processes, memory, processors and temporary space, read from an optional
// profile file.
public sealed class SandboxResources
{
    public int Pids { get; set; } = 128;
    public int MemoryMiB { get; set; } = 768;
    public int Cpus { get; set; } = 2;
    public int SharedMemoryMiB { get; set; } = 128;
    public int TemporaryMiB { get; set; } = 128;
    // Throws InvalidDataException when the file is no resource profile.
    public static SandboxResources Load(string? file)
    {
        // An unknown member is an error rather than ignored, so a misspelled limit cannot silently fall back to a default.
        var strictOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };
        var resource = file == null
            ? new()
            : JsonSerializer.Deserialize<SandboxResources>(File.ReadAllText(file), strictOptions)
                ?? throw new InvalidDataException("Invalid sandbox resource profile.");
        resource.Validate();
        return resource;
    }
    // Throws ArgumentException when a limit is outside the operator profile.
    public void Validate()
    {
        if (Pids is < 32 or > 1024
            || MemoryMiB is < 128 or > 8192
            || Cpus is < 1 or > 8
            || SharedMemoryMiB is < 16 or > 512
            || TemporaryMiB is < 32 or > 1024)
            throw new ArgumentException("Sandbox resource limits are outside the finite local operator profile.");
    }
}
