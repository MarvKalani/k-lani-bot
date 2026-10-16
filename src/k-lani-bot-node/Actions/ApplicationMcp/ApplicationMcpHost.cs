using System.Text.Json.Nodes;
using KLaniBot.Guard;

namespace KLaniBot.Actions.ApplicationMcp;

// Use case: the app_mcp tools of one agent session reach the same running program, so a project FilmCraft opened in one call
// is still open in the next. The host starts a program on first use, keeps one per session and program, and stops it when
// the session ends, after a long idle time or when the node stops. A program that stopped is started fresh on the next call;
// nothing is replayed.
internal sealed class ApplicationMcpHost(NodePolicy policy, TimeProvider? clock = null) : IDisposable
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private const int VersionTimeoutMilliseconds = 10000;
    private const int MaximumVersionLength = 80;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, RunningProgram> _running = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // Throws ArgumentException for a program the policy does not list and ApplicationMcpException when it cannot start.
    public RunningProgram Open(RequestContext caller, string programId)
    {
        ApplicationMcpPolicy settings = policy.FindApplicationMcp(programId)
            ?? throw new ArgumentException("This node offers no program '" + programId + "'.");
        string key = Key(caller, programId);

        lock (_gate)
        {
            StopIdle();
            if (_running.TryGetValue(key, out RunningProgram? running) && !running.Connection.HasExited)
            {
                running.LastUse = _clock.GetUtcNow();
                return running;
            }
            Remove(key);
            running = Launch(settings);
            _running[key] = running;
            return running;
        }
    }

    // A program that failed or missed its deadline is stopped, so the next call starts it fresh.
    public void Forget(RequestContext caller, string programId)
    {
        lock (_gate)
        {
            Remove(Key(caller, programId));
        }
    }

    public void CloseSession(RequestContext caller)
    {
        string prefix = caller.PrincipalId + ":" + caller.SessionId + "|";
        lock (_gate)
        {
            foreach (string key in _running.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                Remove(key);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (string key in _running.Keys.ToList())
                Remove(key);
        }
    }

    // Throws ApplicationMcpException when the program cannot start or does not list its tools.
    private RunningProgram Launch(ApplicationMcpPolicy settings)
    {
        string home = Path.Combine(policy.StateRoot, "application-mcp", settings.Id);
        Directory.CreateDirectory(home);
        string version = ReadVersion(settings);
        ApplicationMcpConnection connection = ApplicationMcpConnection.Start(settings, policy.WorkspaceRoot, home, StartTimeout);
        try
        {
            JsonArray tools = ListTools(connection);
            return new RunningProgram(settings, connection, version, tools) { LastUse = _clock.GetUtcNow() };
        }
        catch (ApplicationMcpException)
        {
            connection.Dispose();
            throw;
        }
    }

    // Throws ApplicationMcpException when the program does not answer tools/list.
    private static JsonArray ListTools(ApplicationMcpConnection connection)
    {
        const int MaximumPages = 10;
        var tools = new JsonArray();
        string? cursor = null;
        for (int page = 0; page < MaximumPages; page++)
        {
            var parameters = cursor == null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            JsonObject result = connection.Request("tools/list", parameters, StartTimeout);
            foreach (JsonNode? tool in result["tools"] as JsonArray ?? [])
                if (tool is JsonObject entry && entry["name"] is JsonValue)
                    tools.Add(entry.DeepClone());
            cursor = result["nextCursor"]?.ToString();
            if (string.IsNullOrEmpty(cursor))
                break;
        }
        return tools;
    }

    // The first nonempty output line, or "unknown" when the program has no version command or it fails.
    private static string ReadVersion(ApplicationMcpPolicy settings)
    {
        if (settings.VersionCommand.Length == 0)
            return "unknown";
        Common.ProcessResult result = Common.ProcessRunner.Run(settings.VersionCommand[0], settings.VersionCommand.Skip(1),
            VersionTimeoutMilliseconds);
        string? line = result.Output.Split('\n').Select(text => text.Trim()).FirstOrDefault(text => text.Length > 0);
        if (!result.Success || line == null)
            return "unknown";
        return line.Length <= MaximumVersionLength ? line : line[..MaximumVersionLength];
    }

    private void StopIdle()
    {
        DateTimeOffset limit = _clock.GetUtcNow() - IdleLimit;
        foreach (string key in _running.Where(entry => entry.Value.LastUse < limit).Select(entry => entry.Key).ToList())
            Remove(key);
    }

    private void Remove(string key)
    {
        if (_running.Remove(key, out RunningProgram? running))
            running.Connection.Dispose();
    }

    private static string Key(RequestContext caller, string programId)
    {
        return caller.PrincipalId + ":" + caller.SessionId + "|" + programId;
    }
}

// One started program of one session: its settings, the open connection, the version it reported and its tool list.
internal sealed class RunningProgram(ApplicationMcpPolicy settings, ApplicationMcpConnection connection, string version, JsonArray tools)
{
    public ApplicationMcpPolicy Settings { get; } = settings;
    public ApplicationMcpConnection Connection { get; } = connection;
    public string Version { get; } = version;
    public JsonArray Tools { get; } = tools;
    public DateTimeOffset LastUse
    {
        get; set;
    }

    public JsonObject? FindTool(string name)
    {
        return Tools.OfType<JsonObject>().FirstOrDefault(tool => tool["name"]?.ToString() == name);
    }
}
