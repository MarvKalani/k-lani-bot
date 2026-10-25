using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Fleet;

namespace KLaniBot.ControllerTray;

// Use case: the operator starts `--controller-tray --state MASTER_STATE` on the computer that runs the pairing master. The
// service asks the master for controller-status every two seconds and sends the command the person chose over the master's
// local administration pipe. Pipe calls run on background threads, so the tray menu never waits for the master.
internal sealed class ControllerTrayService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly string _stateDirectory;
    private readonly ControllerTrayHost _host;
    private readonly SemaphoreSlim _refreshing = new(1);
    private readonly CancellationToken _cancellation;
    private string _lastFailure = "";
    private string _shownStamp = "";

    private ControllerTrayService(string stateDirectory, ControllerTrayHost host, CancellationToken cancellation)
    {
        _stateDirectory = stateDirectory;
        _host = host;
        _cancellation = cancellation;
    }

    // Blocks the calling thread, which becomes the tray's UI thread, until the person closes the tray or the process is
    // cancelled. Throws what ControllerTrayHost.Create throws when no tray can be shown.
    public static int Run(string stateDirectory, CancellationToken cancellation)
    {
        using ControllerTrayHost host = ControllerTrayHost.Create();
        var service = new ControllerTrayService(Path.GetFullPath(stateDirectory), host, cancellation);
        host.Chosen += command => _ = service.ExecuteAsync(command);
        using CancellationTokenRegistration closing = cancellation.Register(host.Close);

        Task polling = Task.Run(service.PollAsync, CancellationToken.None);
        host.Run();
        host.Close();
        polling.Wait(TimeSpan.FromSeconds(5));
        return 0;
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            await RefreshAsync();
        }
        while (!_cancellation.IsCancellationRequested && await WaitForTickAsync(timer));
    }

    private async Task<bool> WaitForTickAsync(PeriodicTimer timer)
    {
        try
        {
            return await timer.WaitForNextTickAsync(_cancellation);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RefreshAsync()
    {
        await _refreshing.WaitAsync(CancellationToken.None);
        try
        {
            ControllerStatus? status = await ReadStatusAsync();
            ControllerTrayView view = ControllerTrayMenu.Build(status, RegisteredNodes(), _lastFailure);
            string stamp = view.Stamp();
            if (stamp != _shownStamp)
            {
                _shownStamp = stamp;
                _host.Show(view);
                Console.WriteLine("Controller tray: " + Summary(status));
            }
        }
        finally
        {
            _refreshing.Release();
        }
    }

    // Null when the master does not answer: it is not running, belongs to another user or state directory, or is older.
    private async Task<ControllerStatus?> ReadStatusAsync()
    {
        try
        {
            JsonObject reply = await MasterAdministration.CallAsync(_stateDirectory, "controller-status", _cancellation);
            return ControllerStatus.TryRead(reply, out ControllerStatus status) ? status : null;
        }
        catch (Exception failure) when (failure is IOException or OperationCanceledException or TimeoutException
            or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // The log line tells an operator watching the terminal, and the tests, what the tray shows.
    private static string Summary(ControllerStatus? status)
    {
        if (status == null)
        {
            return "master not running";
        }
        int connected = status.Nodes.Count(node => node.Connected);
        int paused = status.Clients.Count(client => client.State == ControllerClientState.Paused);
        return "bot " + ControllerStatus.WireName(status.Bot) + ", " + connected + " of " + status.Nodes.Count + " nodes connected, "
            + status.Clients.Count + " MCP clients, " + paused + " paused";
    }

    // The master's registry, read only to name the nodes while the master does not answer; an unreadable file names none.
    private IReadOnlyList<string> RegisteredNodes()
    {
        string registry = Path.Combine(_stateDirectory, "nodes.json");
        try
        {
            JsonObject? nodes = JsonNode.Parse(File.ReadAllText(registry))?["nodes"] as JsonObject;
            return nodes?.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToList() ?? [];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private async Task ExecuteAsync(ControllerTrayCommand command)
    {
        string? masterCommand = command.MasterCommand();
        if (masterCommand == null)
        {
            _host.Close();
            return;
        }

        _lastFailure = command.Action == ControllerTrayAction.OpenVitalsReport
            ? await OpenReportAsync(masterCommand)
            : await SendAsync(masterCommand);
        await RefreshAsync();
    }

    // The master writes the report into its state directory; only a regular file there is handed to the default browser.
    // Returns the reason it could not be opened, or an empty text.
    private async Task<string> OpenReportAsync(string masterCommand)
    {
        try
        {
            JsonObject reply = await MasterAdministration.CallAsync(_stateDirectory, masterCommand, _cancellation);
            string? path = reply["path"]?.ToString();
            string expected = Path.Combine(_stateDirectory, NodeVitalsReport.FileName);
            if (path != expected || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return reply["error"]?.ToString() ?? masterCommand;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
            return "";
        }
        catch (Exception failure) when (failure is IOException or OperationCanceledException or TimeoutException
            or UnauthorizedAccessException or JsonException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return failure.Message;
        }
    }

    // Returns the master's reason for a refusal, or an empty text on success.
    private async Task<string> SendAsync(string masterCommand)
    {
        try
        {
            JsonObject reply = await MasterAdministration.CallAsync(_stateDirectory, masterCommand, _cancellation);
            bool succeeded = reply["success"] is JsonValue flag && flag.TryGetValue(out bool value) && value;
            return succeeded ? "" : reply["error"]?.ToString() ?? masterCommand;
        }
        catch (Exception failure) when (failure is IOException or OperationCanceledException or TimeoutException
            or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return failure.Message;
        }
    }
}
