using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Audit;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Inventory;
using KLaniBot.Localization;
using KLaniBot.Transport;

namespace KLaniBot.Fleet;

// Use case: from the master's console the operator configures a target and catalogue, scans a node's inventory
// through its relay, shows the resulting compatibility report to the person at that node for local review, and
// can collect a screenshot as evidence. Each step runs as a job whose status the console can query.
//
// The master can only propose: approval comes from the person at the node, and a report is offered for approval
// only when it has no blockers. Before the review starts and again when the answer arrives, the report is checked
// against the current target, catalogue and inventory; if anything changed, the result is Invalidated and no
// approval is recorded. Inventory and screenshots are gathered through the node's normal consent, and a
// screenshot is stored as untrusted evidence with its hash.
public sealed class MasterServiceWorkflows
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan SessionCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConsentPollInterval = TimeSpan.FromSeconds(1);
    private const int InventoryPageSize = 100;
    private const int MaximumApplications = 4096;
    private const int MaximumReportedBlockers = 100;
    private const int MaximumEvidenceBytes = 2 * 1024 * 1024;

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonObject> _jobs = new();
    private readonly HashSet<string> _activeJobs = new();
    private readonly CancellationToken _lifetime;
    private readonly AuditJournal _journal;

    public MasterServiceWorkflows(string stateDirectory, CancellationToken lifetime)
    {
        _directory = Path.Combine(stateDirectory, "service-workflows");
        PairingClient.CreatePrivateDirectory(_directory);
        _lifetime = lifetime;
        _journal = new AuditJournal(_directory);
    }

    // Throws InvalidDataException when the catalogue is too large or the target scope is not explicit.
    public JsonObject Configure(string targetPath, string cataloguePath)
    {
        CompatibilityTarget target = Read<CompatibilityTarget>(targetPath);
        CompatibilityRule[] catalogue = Read<CompatibilityRule[]>(cataloguePath);
        if (catalogue.Length > 10000 || target.RequiredInventorySources.Length == 0)
        {
            throw new InvalidDataException("Bounded catalogue and explicit inventory scope required.");
        }
        Write("target.json", JsonSerializer.Serialize(target, SystemInventory.JsonOptions));
        Write("catalogue.json", JsonSerializer.Serialize(catalogue, SystemInventory.JsonOptions));
        return new JsonObject { ["success"] = true, ["target"] = target.Id };
    }

    public JsonObject StartScan(string node, ReverseRelay relay)
    {
        CompatibilityTarget target = Read<CompatibilityTarget>(Path.Combine(_directory, "target.json"));
        CompatibilityRule[] catalogue = Read<CompatibilityRule[]>(Path.Combine(_directory, "catalogue.json"));
        string jobId = CreateJob(node, "collecting");
        _ = ScanAsync(jobId, node, relay, target, catalogue);
        return new JsonObject { ["success"] = true, ["job_id"] = jobId };
    }

    // Throws InvalidDataException when the node denies the inventory or its pages change while being read.
    private async Task ScanAsync(string jobId, string node, ReverseRelay relay, CompatibilityTarget target, CompatibilityRule[] catalogue)
    {
        string session = Guid.CreateVersion7().ToString();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        deadline.CancelAfter(JobTimeout);
        try
        {
            SystemInventory inventory = await CollectInventoryAsync(relay, session, deadline.Token);
            CompatibilityReport report = CompatibilityAssessment.Evaluate(node, inventory, target, catalogue);
            Write(report.Id + ".inventory.json", JsonSerializer.Serialize(inventory, SystemInventory.JsonOptions));
            Write(report.Id + ".report.json", JsonSerializer.Serialize(report, SystemInventory.JsonOptions));
            var latest = new JsonObject { ["fingerprint"] = inventory.Fingerprint };
            Write("latest-" + PairingProtocol.Hash(node) + ".json", latest.ToJsonString());
            FinishJob(jobId, ScanResult(report));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException
                or ArgumentException or OperationCanceledException)
        {
            FinishJob(jobId, new JsonObject { ["success"] = false, ["state"] = "failed", ["error"] = exception.Message });
        }
        finally
        {
            await CloseSessionAsync(relay, session);
        }
    }

    // Pages are read until the node reports no next offset; every page must belong to the same inventory.
    // Throws InvalidDataException when the node refuses, the inventory changes between pages or exceeds its bounds.
    private static async Task<SystemInventory> CollectInventoryAsync(ReverseRelay relay, string session, CancellationToken cancellation)
    {
        JsonObject first = await CollectPageAsync(relay, session, new JsonObject { ["limit"] = InventoryPageSize }, cancellation);
        var applications = new List<InstalledApplication>();
        JsonObject page = first;
        while (true)
        {
            if (page["success"]?.GetValue<bool>() != true)
            {
                throw new InvalidDataException("Inventory denied or unavailable; grant this session locally and start a fresh scan.");
            }
            applications.AddRange(page["applications"]!.Deserialize<InstalledApplication[]>(SystemInventory.JsonOptions)!);
            if (applications.Count > MaximumApplications || page["fingerprint"]?.ToString() != first["fingerprint"]?.ToString())
            {
                throw new InvalidDataException("Inventory pagination changed or exceeded bounds.");
            }
            if (page["next_offset"] == null)
            {
                break;
            }
            page = await CollectPageAsync(relay, session, new JsonObject
            {
                ["inventory_id"] = first["inventory_id"]!.ToString(),
                ["offset"] = page["next_offset"]!.GetValue<int>(),
                ["limit"] = InventoryPageSize
            }, cancellation);
        }

        return new SystemInventory(
            first["inventory_id"]!.ToString(),
            DateTimeOffset.Parse(first["captured_at"]!.ToString()),
            first["machine"]!.Deserialize<InventoryMachine>(SystemInventory.JsonOptions)!,
            applications.ToArray(),
            first["sources"]!.Deserialize<InventorySource[]>(SystemInventory.JsonOptions)!,
            first["truncated"]!.GetValue<bool>(),
            first["fingerprint"]!.ToString());
    }

    private static JsonObject ScanResult(CompatibilityReport report)
    {
        var blockers = report.Blockers.Take(MaximumReportedBlockers).Select(blocker => (JsonNode?)JsonValue.Create(blocker)).ToArray();
        return new JsonObject
        {
            ["success"] = true,
            ["state"] = report.Blockers.Length == 0 ? "AwaitingLocalReview" : "Blocked",
            ["assessment_id"] = report.Id,
            ["report_fingerprint"] = report.Fingerprint,
            ["application_count"] = report.Findings.Length,
            ["blocker_count"] = report.Blockers.Length,
            ["blockers"] = new JsonArray(blockers)
        };
    }

    private static async Task CloseSessionAsync(ReverseRelay relay, string session)
    {
        using var closeDeadline = new CancellationTokenSource(SessionCloseTimeout);
        await relay.CallAsync(new JsonObject
        {
            ["kind"] = "presence",
            ["session"] = session,
            ["body"] = new JsonObject { ["event"] = "close" }
        }, closeDeadline.Token);
    }

    private static async Task<JsonObject> CollectPageAsync(
        ReverseRelay relay,
        string session,
        JsonObject arguments,
        CancellationToken cancellation)
    {
        while (true)
        {
            JsonObject result = await relay.CallAsync(new JsonObject
            {
                ["kind"] = "action",
                ["session"] = session,
                ["body"] = new JsonObject { ["action"] = ToolNames.Of(Tool.CollectInventory), ["args"] = arguments }
            }, cancellation);
            if (!ActionErrorCodeNames.Is(result, ActionErrorCode.ConsentRequired))
            {
                return result;
            }
            // Only retry a read that was explicitly not dispatched; keep its pending local prompt alive.
            await Task.Delay(1000, cancellation);
        }
    }

    // Throws UnauthorizedAccessException when the report no longer matches node, target, catalogue or inventory, and ArgumentException for
    // a message beyond the display limit.
    public JsonObject StartReview(string node, string reportId, ReverseRelay relay, string? message = null)
    {
        CompatibilityReport report = LoadReport(reportId);
        string currentTargetFingerprint = CompatibilityAssessment.Hash(Read<CompatibilityTarget>(Path.Combine(_directory, "target.json")));
        if (report.Node != node || report.TargetFingerprint != currentTargetFingerprint)
        {
            throw new UnauthorizedAccessException("Report belongs to another node or the target changed; scan again.");
        }
        CompatibilityRule[] catalogue = Read<CompatibilityRule[]>(Path.Combine(_directory, "catalogue.json"));
        SystemInventory inventory = Read<SystemInventory>(Path.Combine(_directory, report.Id + ".inventory.json"));
        if (CompatibilityAssessment.Evaluate(node, inventory, report.Target, catalogue).Fingerprint != report.Fingerprint)
        {
            throw new UnauthorizedAccessException("Catalogue changed or report no longer matches its inventory.");
        }
        string summary = message ?? FormatReview(report);
        if (summary.Length is < 1 or > 12000)
        {
            throw new ArgumentException("Service message exceeds the display limit.");
        }
        string jobId = CreateJob(node, "AwaitingLocalReview");
        _ = ReviewAsync(jobId, report, relay, summary, message != null);
        return new JsonObject { ["success"] = true, ["job_id"] = jobId };
    }

    private async Task ReviewAsync(string jobId, CompatibilityReport report, ReverseRelay relay, string text, bool messageOnly)
    {
        try
        {
            JsonObject result = await relay.CallAsync(new JsonObject
            {
                ["kind"] = "service_check",
                ["body"] = new JsonObject
                {
                    ["type"] = messageOnly ? "message" : "review",
                    ["text"] = text,
                    ["report_fingerprint"] = report.Fingerprint,
                    ["inventory_fingerprint"] = report.InventoryFingerprint,
                    ["allow_approval"] = !messageOnly && report.Blockers.Length == 0
                }
            }, _lifetime, TimeSpan.FromSeconds(295));
            bool valid = result["success"]?.GetValue<bool>() == true
                && result["report_fingerprint"]?.ToString() == report.Fingerprint
                && result["inventory_fingerprint"]?.ToString() == report.InventoryFingerprint;
            valid = valid && IsCurrent(report);
            bool approvable = !messageOnly && report.Blockers.Length == 0 && result["approved"]?.GetValue<bool>() == true;
            ServiceAssessmentState state = DecideAssessmentState(valid, approvable);
            result["state"] = state.ToString();
            result["assessment_id"] = report.Id;
            result["node"] = report.Node;
            Write(jobId + ".receipt.json", result.ToJsonString());
            FinishJob(jobId, result);
        }
        catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException)
        {
            FinishJob(jobId, new JsonObject
            {
                ["success"] = false,
                ["state"] = "Expired",
                ["error"] = "Review unavailable; no approval recorded."
            });
        }
    }

    // A review that no longer matches the report or the current inventory records nothing but invalidation.
    private static ServiceAssessmentState DecideAssessmentState(bool valid, bool approvable)
    {
        if (!valid)
        {
            return ServiceAssessmentState.Invalidated;
        }
        return approvable ? ServiceAssessmentState.Approved : ServiceAssessmentState.Rejected;
    }

    // Throws UnauthorizedAccessException when the report is not the node's current assessment.
    public JsonObject StartScreenshot(string node, string reportId, ReverseRelay relay)
    {
        CompatibilityReport report = LoadReport(reportId);
        if (report.Node != node || !IsCurrent(report))
        {
            throw new UnauthorizedAccessException("Screenshot must belong to the current node assessment.");
        }
        string jobId = CreateJob(node, "collecting_evidence");
        _ = CaptureScreenshotAsync(jobId, report, relay);
        return new JsonObject { ["success"] = true, ["job_id"] = jobId };
    }

    // Throws InvalidDataException when the node refuses the screenshot or returns no bounded PNG.
    private async Task CaptureScreenshotAsync(string jobId, CompatibilityReport report, ReverseRelay relay)
    {
        string session = Guid.CreateVersion7().ToString();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        deadline.CancelAfter(JobTimeout);
        try
        {
            JsonObject result = await TakeScreenshotAsync(relay, session, deadline.Token);
            byte[] image = PngEvidence(result);
            string path = Path.Combine(_directory, jobId + ".png");
            CartridgeManifest.RejectLinks(path);
            PrivateFile.WriteAllBytes(path, image);
            JsonObject evidence = EvidenceRecord(report, image);
            Write(jobId + ".evidence.json", evidence.ToJsonString());
            FinishJob(jobId, evidence);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException
                or FormatException or OperationCanceledException)
        {
            FinishJob(jobId, new JsonObject { ["success"] = false, ["state"] = "failed", ["error"] = exception.Message });
        }
        finally
        {
            await CloseSessionAsync(relay, session);
        }
    }

    // The person at the node decides; until then the node answers ConsentRequired and the master asks again.
    private static async Task<JsonObject> TakeScreenshotAsync(ReverseRelay relay, string session, CancellationToken cancellation)
    {
        JsonObject result;
        do
        {
            result = await relay.CallAsync(new JsonObject
            {
                ["kind"] = "action",
                ["session"] = session,
                ["body"] = new JsonObject { ["action"] = ToolNames.Of(Tool.TakeScreenshot), ["args"] = new JsonObject() }
            }, cancellation);
            if (ActionErrorCodeNames.Is(result, ActionErrorCode.ConsentRequired))
            {
                await Task.Delay(ConsentPollInterval, cancellation);
            }
        }
        while (ActionErrorCodeNames.Is(result, ActionErrorCode.ConsentRequired));
        return result;
    }

    // Throws InvalidDataException when the screenshot was refused or is no PNG within the size limit.
    private static byte[] PngEvidence(JsonObject result)
    {
        if (result["success"]?.GetValue<bool>() != true || result["mime_type"]?.ToString() != "image/png")
        {
            throw new InvalidDataException("Screenshot was denied or unsupported.");
        }

        byte[] image = Convert.FromBase64String(result["image"]!.ToString());
        byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        bool bounded = image.Length >= pngSignature.Length && image.Length <= MaximumEvidenceBytes;
        if (!bounded || !image.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature))
        {
            throw new InvalidDataException("Invalid bounded PNG evidence.");
        }
        return image;
    }

    private static JsonObject EvidenceRecord(CompatibilityReport report, byte[] image)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["state"] = "evidence_saved",
            ["assessment_id"] = report.Id,
            ["report_fingerprint"] = report.Fingerprint,
            ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image)),
            ["bytes"] = image.Length,
            ["data_trust"] = "untrusted",
            ["captured_at"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    public JsonObject Status(string jobId)
    {
        RequireId(jobId);
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out JsonObject? job))
            {
                JsonObject current = (JsonObject)job.DeepClone();
                if (current["state"]?.ToString() == "Approved" && !IsCurrent(LoadReport(current["assessment_id"]!.ToString())))
                {
                    current["state"] = "Invalidated";
                }
                return current;
            }
        }
        string path = Path.Combine(_directory, jobId + ".job.json");
        JsonObject persisted = Read<JsonObject>(path);
        if (persisted["state"]?.ToString() == "Approved" && !IsCurrent(LoadReport(persisted["assessment_id"]!.ToString())))
        {
            persisted["state"] = "Invalidated";
        }
        return persisted;
    }

    public CompatibilityReport LoadReport(string reportId)
    {
        RequireId(reportId);
        return Read<CompatibilityReport>(Path.Combine(_directory, reportId + ".report.json"));
    }

    private bool IsCurrent(CompatibilityReport report)
    {
        CompatibilityTarget target = Read<CompatibilityTarget>(Path.Combine(_directory, "target.json"));
        CompatibilityRule[] catalogue = Read<CompatibilityRule[]>(Path.Combine(_directory, "catalogue.json"));
        SystemInventory inventory = Read<SystemInventory>(Path.Combine(_directory, report.Id + ".inventory.json"));
        JsonObject latest = Read<JsonObject>(Path.Combine(_directory, "latest-" + PairingProtocol.Hash(report.Node) + ".json"));
        CompatibilityReport expected = CompatibilityAssessment.Evaluate(report.Node, inventory, target, catalogue);
        return latest["fingerprint"]?.ToString() == report.InventoryFingerprint && expected.Fingerprint == report.Fingerprint &&
            CompatibilityAssessment.Hash(expected.Findings) == CompatibilityAssessment.Hash(report.Findings) &&
            CompatibilityAssessment.Hash(expected.Blockers) == CompatibilityAssessment.Hash(report.Blockers);
    }

    // Throws InvalidOperationException when the job limit is reached.
    private string CreateJob(string node, string state)
    {
        string id = Guid.CreateVersion7().ToString();
        lock (_gate)
        {
            if (_activeJobs.Count >= 32)
            {
                throw new InvalidOperationException("Concurrent service job limit reached.");
            }
            while (_jobs.Count >= 64)
            {
                string completed = _jobs.Keys.First(key => !_activeJobs.Contains(key));
                _jobs.Remove(completed);
            }
            _activeJobs.Add(id);
            _jobs[id] = new JsonObject { ["success"] = true, ["job_id"] = id, ["node"] = node, ["state"] = state };
        }
        return id;
    }

    private void FinishJob(string id, JsonObject result)
    {
        result["job_id"] = id;
        Write(id + ".job.json", result.ToJsonString());
        lock (_gate)
        {
            _journal.Append(new JsonObject
            {
                ["event"] = "service_job_finished",
                ["result"] = result.DeepClone(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            });
            _jobs[id] = result;
            _activeJobs.Remove(id);
        }
    }

    // Throws ArgumentException when the value is not a canonical UUIDv7.
    private static void RequireId(string value)
    {
        if (!Guid.TryParse(value, out Guid id) || id.Version != 7 || id.ToString() != value)
        {
            throw new ArgumentException("Canonical UUIDv7 required for service record lookup.");
        }
    }

    // Throws InvalidDataException when the file is too large or not the expected record.
    private static T Read<T>(string path)
    {
        CartridgeManifest.RejectLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Service file too large.");
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), SystemInventory.JsonOptions)
            ?? throw new InvalidDataException("Invalid service file.");
    }

    private void Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        PairingProtocol.WritePrivate(path + ".new", content, true);
        CartridgeManifest.RejectLinks(path);
        File.Move(path + ".new", path, true);
    }

    // Throws InvalidDataException when the text exceeds what the local review window shows.
    private static string FormatReview(CompatibilityReport report)
    {
        // Names and versions come from the node, so they are quoted as JSON strings to keep one finding on one line.
        string findings = string.Join("\r\n", report.Findings.Select(finding =>
            JsonSerializer.Serialize(finding.ApplicationName) + " " + JsonSerializer.Serialize(finding.Version) + ": " + finding.Verdict));
        string blockers = string.Join("\r\n", report.Blockers);
        string text = UiTexts.Format(UiText.ReviewTarget, report.Target.Description) + "\r\n"
            + UiTexts.Format(UiText.ReviewReport, report.Id) + "\r\n"
            + UiTexts.Format(UiText.ReviewInventory, report.InventoryFingerprint) + "\r\n\r\n"
            + findings + "\r\n\r\n"
            + UiTexts.Get(UiText.ReviewBlockers) + "\r\n" + blockers;
        if (text.Length > 12000)
        {
            throw new InvalidDataException("Report exceeds local display limits; split the assessment before a complete local review.");
        }
        return text;
    }
}
