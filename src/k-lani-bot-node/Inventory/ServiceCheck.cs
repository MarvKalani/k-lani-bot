using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Fleet;
using KLaniBot.Transport;

namespace KLaniBot.Inventory;

// A master's service check reaches the node over the reverse connection; the person at the node reviews it in a window of its own while
// every other action waits, and the answer is bound to the report and the inventory it was about.
public static class ServiceCheck
{
    // Throws InvalidDataException for an invalid request or a response for another report, and IOException when the review window does not
    // start or answer.
    public static async Task<JsonObject> RunAsync(JsonObject request, string stateDirectory, CancellationToken cancellation)
    {
        string fingerprint = ValidatedReportFingerprint(request);
        if (!OperatingSystem.IsWindows())
        {
            return new JsonObject { ["success"] = false, ["error"] = "Local service-check UI currently requires Windows." };
        }
        SystemInventory before = SystemInventoryCollector.Collect();
        if (before.Fingerprint != request["inventory_fingerprint"]!.ToString())
        {
            return Invalidated("Inventory changed before local review.");
        }

        string directory = Path.Combine(stateDirectory, "service-checks");
        PairingClient.CreatePrivateDirectory(directory);
        string path = Path.Combine(directory, Guid.CreateVersion7() + ".json");
        PairingProtocol.WritePrivate(path, request.ToJsonString());
        using Process process = StartReviewWindow(path, stateDirectory);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ReviewTimeout);
        try
        {
            JsonObject result = await ReadReviewAsync(process, errors, fingerprint, deadline.Token);
            SystemInventory after = SystemInventoryCollector.Collect();
            if (after.Fingerprint != before.Fingerprint)
            {
                return Invalidated("Inventory changed during local review.");
            }
            result["inventory_fingerprint"] = after.Fingerprint;
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
            File.Delete(path);
        }
    }

    // The review is tied to one inventory; a change before or during it makes the answer meaningless.
    private static JsonObject Invalidated(string error)
    {
        return new JsonObject { ["success"] = false, ["state"] = "Invalidated", ["error"] = error };
    }

    private const int Sha256HexLength = 64;
    private const int MaximumTextLength = 12000;
    private const int MaximumNotesLength = 4000;
    private const int RequestFields = 5;
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromSeconds(280);

    // Throws InvalidDataException for a request that is not a bounded review or message for one report.
    private static string ValidatedReportFingerprint(JsonObject request)
    {
        string kind = request["type"]?.ToString() ?? "";
        string text = request["text"]?.ToString() ?? "";
        string fingerprint = request["report_fingerprint"]?.ToString() ?? "";

        if (request.Count != RequestFields || kind is not ("review" or "message") || text.Length is < 1 or > MaximumTextLength ||
            fingerprint.Length != Sha256HexLength || request["inventory_fingerprint"]?.ToString().Length != Sha256HexLength)
        {
            throw new InvalidDataException("Invalid bounded service-check request.");
        }
        return fingerprint;
    }

    // The review window is a separate process in the person's session, started like the actuator.
    // Throws IOException when the window cannot start.
    private static Process StartReviewWindow(string requestPath, string stateDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--service-check");
        start.ArgumentList.Add(requestPath);
        WorkerEnvironment.Configure(start, stateDirectory, WorkerPlatform.Windows);

        return Process.Start(start) ?? throw new IOException("Local service-check window could not start.");
    }

    // Throws IOException when the window answers nothing and InvalidDataException when its answer is for another report.
    private static async Task<JsonObject> ReadReviewAsync(Process process, Task<string> errors, string fingerprint,
        CancellationToken cancellation)
    {
        var reader = new JsonLineReader(process.StandardOutput.BaseStream);
        string response = await reader.ReadAsync(cancellation)
            ?? throw new IOException("No local service-check response.");
        JsonObject result = JsonNode.Parse(response)!.AsObject();
        await process.WaitForExitAsync(cancellation);
        await errors;

        if (process.ExitCode != 0
            || result["report_fingerprint"]?.ToString() != fingerprint
            || result["notes"]?.ToString().Length > MaximumNotesLength)
        {
            throw new InvalidDataException("Local service-check response does not match the assigned report.");
        }
        return result;
    }
}
