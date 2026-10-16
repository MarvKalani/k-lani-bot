using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Common;

namespace KLaniBot.Actions;

// The local record of what the node did, with screenshots, for the person at the machine (index.html,
// opened from the indicator). Screenshots can show the whole desktop, including personal data, so
// everything here is private (files 0600, directories 0700) and bounded in count and size.
//
// Executed steps and refused requests have separate budgets: refusals cost the caller nothing, so a
// client could otherwise push the record of real work out of the trace with a stream of bad requests.
// A refused request is in the journal with the same content, so its trace file can simply be dropped.
public sealed class ActionTrace
{
    public const int ExecutedStepLimit = 200;
    public const int RejectedStepLimit = 50;
    public const int ArchivedStepLimit = 200;
    private const int ImagesPerStep = 2;
    private const long ExecutedByteLimit = 128L * 1024 * 1024;
    private const long ArchivedByteLimit = 128L * 1024 * 1024;
    private const int MaximumImageBytes = 4 * 1024 * 1024;
    private const string RejectedFolder = "rejected";
    private const string ArchiveFolder = "archive";

    private readonly string _directory;
    private readonly string _rejectedDirectory;
    private readonly string _archiveDirectory;
    private readonly TraceRetention _retention;
    private readonly Action<JsonObject> _audit;
    private readonly ActionTraceViewer _viewer;

    public ActionTrace(string stateRoot, TraceRetention retention, Action<JsonObject> audit)
    {
        _directory = Path.Combine(stateRoot, "action-traces");
        _rejectedDirectory = Path.Combine(_directory, RejectedFolder);
        _archiveDirectory = Path.Combine(_directory, ArchiveFolder);
        _retention = retention;
        _audit = audit;
        _viewer = new ActionTraceViewer(_directory, _rejectedDirectory);
    }

    // Throws ArgumentException for an invalid operation id or phase and IOException when the image exceeds the trace budget.
    public string SaveScreenshot(string operationId, string phase, byte[] image)
    {
        Guid identifier = ReadOperationId(operationId);
        if (phase is not ("before" or "after"))
        {
            throw new ArgumentException("Invalid trace identifier or phase.");
        }
        if (image.Length > MaximumImageBytes)
        {
            throw new IOException("Screenshot exceeds the local trace limit.");
        }
        PrepareDirectory(_directory);
        MakeRoomForExecuted(identifier, image.Length, addsStep: false, addsImage: true);
        string name = identifier + "-" + phase + ".png";
        WritePrivate(Path.Combine(_directory, name), image);
        return name;
    }

    public string SaveStep(string operationId, JsonObject receipt, JsonObject screenshots)
    {
        Guid identifier = ReadOperationId(operationId);
        PrepareDirectory(_directory);
        byte[] content = StepContent(receipt, screenshots);
        MakeRoomForExecuted(identifier, content.Length, addsStep: true, addsImage: false);
        string name = identifier + ".json";
        WritePrivate(Path.Combine(_directory, name), content);
        _viewer.Write();
        return name;
    }

    // Refused requests never carry screenshots: nothing reached the desktop that could be shown.
    public string SaveRejectedStep(string operationId, JsonObject rejection)
    {
        Guid identifier = ReadOperationId(operationId);
        PrepareDirectory(_directory);
        PrepareDirectory(_rejectedDirectory);
        MakeRoomForRejected();
        string name = identifier + ".json";
        WritePrivate(Path.Combine(_rejectedDirectory, name), StepContent(rejection, new JsonObject()));
        _viewer.Write();
        return RejectedFolder + "/" + name;
    }

    // Throws ArgumentException when the id is not a UUIDv7.
    private static Guid ReadOperationId(string operationId)
    {
        bool valid = Guid.TryParse(operationId, out Guid identifier) && identifier.Version == 7;
        if (!valid)
        {
            throw new ArgumentException("Invalid trace identifier.");
        }
        return identifier;
    }

    private static byte[] StepContent(JsonObject receipt, JsonObject screenshots)
    {
        var step = new JsonObject
        {
            ["version"] = 1,
            ["receipt"] = receipt.DeepClone(),
            ["screenshots"] = screenshots.DeepClone()
        };
        return Encoding.UTF8.GetBytes(step.ToJsonString());
    }

    // State is outside the agent workspace. Links are refused so that a trace write cannot become an
    // arbitrary file write.
    // Throws IOException when a part of the trace directory is a link.
    private static void PrepareDirectory(string directory)
    {
        for (string? path = directory; path != null; path = Path.GetDirectoryName(path))
        {
            bool isLink = Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            if (isLink)
            {
                throw new IOException("Trace directory links are forbidden.");
            }
        }
        PrivateFile.CreateDirectory(directory);
    }

    // The step being recorded is never displaced: its before image may already be on disk while its
    // after image and its JSON are still to come.
    // Throws IOException when the budget is full of steps that may not be displaced, or one step alone exceeds it.
    private void MakeRoomForExecuted(Guid current, long addedBytes, bool addsStep, bool addsImage)
    {
        while (true)
        {
            List<TraceStepFiles> steps = TraceStepFiles.InDirectory(_directory);
            bool full = IsExecutedBudgetFull(steps, addedBytes, addsStep, addsImage);
            if (!full)
            {
                return;
            }
            if (_retention == TraceRetention.Stop)
            {
                throw new IOException("Local trace budget reached. Archive traces before recording more.");
            }
            TraceStepFiles? oldest = steps.FirstOrDefault(step => step.OperationId != current);
            if (oldest == null)
            {
                throw new IOException("A single trace step exceeds the local trace budget.");
            }
            MoveToArchive(oldest);
        }
    }

    private static bool IsExecutedBudgetFull(List<TraceStepFiles> steps, long addedBytes, bool addsStep, bool addsImage)
    {
        int stepCount = steps.Count(step => step.HasJson);
        int imageCount = steps.Sum(step => step.ImageCount);
        long storedBytes = steps.Sum(step => step.Bytes);
        bool stepsFull = addsStep && stepCount >= ExecutedStepLimit;
        bool imagesFull = addsImage && imageCount >= ExecutedStepLimit * ImagesPerStep;
        bool bytesFull = storedBytes + addedBytes > ExecutedByteLimit;
        return stepsFull || imagesFull || bytesFull;
    }

    // Throws IOException when the budget for refused requests is full.
    private void MakeRoomForRejected()
    {
        List<TraceStepFiles> steps = TraceStepFiles.InDirectory(_rejectedDirectory);
        if (steps.Count < RejectedStepLimit)
        {
            return;
        }
        if (_retention == TraceRetention.Stop)
        {
            throw new IOException("Local trace budget for refused requests reached. Archive traces before recording more.");
        }
        int surplus = steps.Count - RejectedStepLimit + 1;
        foreach (TraceStepFiles step in steps.Take(surplus))
        {
            step.Delete();
        }
    }

    private void MoveToArchive(TraceStepFiles step)
    {
        PrepareDirectory(_archiveDirectory);
        var files = new JsonArray();
        foreach (string path in step.Paths)
        {
            files.Add(DescribeFile(path));
            File.Move(path, Path.Combine(_archiveDirectory, Path.GetFileName(path)));
        }
        List<TraceStepFiles> archived = TraceStepFiles.InDirectory(_archiveDirectory);
        _audit(new JsonObject
        {
            ["event"] = "trace_rotated",
            ["operation_id"] = step.OperationId.ToString(),
            ["files"] = files,
            ["archived_steps"] = archived.Count,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        });
        TrimArchive(archived);
    }

    // The archive is bounded as well. What leaves it is gone; the journal entry of its rotation keeps
    // the digests, and this entry records when it was deleted.
    private void TrimArchive(List<TraceStepFiles> archived)
    {
        while (archived.Count > ArchivedStepLimit || archived.Sum(step => step.Bytes) > ArchivedByteLimit)
        {
            TraceStepFiles oldest = archived[0];
            archived.RemoveAt(0);
            var files = new JsonArray();
            foreach (string path in oldest.Paths)
            {
                files.Add(Path.GetFileName(path));
            }
            oldest.Delete();
            _audit(new JsonObject
            {
                ["event"] = "trace_discarded",
                ["operation_id"] = oldest.OperationId.ToString(),
                ["files"] = files,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            });
        }
    }

    private static JsonObject DescribeFile(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        return new JsonObject
        {
            ["file"] = Path.GetFileName(path),
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(content)),
            ["bytes"] = content.Length
        };
    }

    private static void WritePrivate(string path, byte[] content)
    {
        using var stream = PrivateFile.OpenWrite(path, FileMode.CreateNew, FileShare.Read);
        stream.Write(content);
        stream.Flush(true);
    }
}

// The files of one step, found by the operation id at the start of their names. Steps are ordered by
// the time their first file was written: UUIDv7 ids from .NET are random within one millisecond, so
// their order alone does not say which step came first. A move to the archive keeps that time.
internal sealed class TraceStepFiles
{
    private const int IdentifierLength = 36;

    private TraceStepFiles(Guid operationId)
    {
        OperationId = operationId;
    }

    public Guid OperationId
    {
        get;
    }
    public List<string> Paths { get; } = [];
    public bool HasJson => Paths.Any(path => path.EndsWith(".json", StringComparison.Ordinal));
    public int ImageCount => Paths.Count(path => path.EndsWith(".png", StringComparison.Ordinal));
    public long Bytes
    {
        get; private set;
    }
    public DateTime FirstWritten { get; private set; } = DateTime.MaxValue;

    public static List<TraceStepFiles> InDirectory(string directory)
    {
        var steps = new Dictionary<string, TraceStepFiles>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return [];
        }
        foreach (string path in Directory.GetFiles(directory))
        {
            string name = Path.GetFileName(path);
            bool isStepFile = name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".png", StringComparison.Ordinal);
            if (!isStepFile || name.Length < IdentifierLength || !Guid.TryParse(name[..IdentifierLength], out Guid identifier))
            {
                continue;
            }
            string key = name[..IdentifierLength];
            if (!steps.TryGetValue(key, out TraceStepFiles? step))
            {
                step = new TraceStepFiles(identifier);
                steps[key] = step;
            }
            var file = new FileInfo(path);
            step.Paths.Add(path);
            step.Bytes += file.Length;
            if (file.LastWriteTimeUtc < step.FirstWritten)
            {
                step.FirstWritten = file.LastWriteTimeUtc;
            }
        }
        return steps.Values
            .OrderBy(step => step.FirstWritten)
            .ThenBy(step => step.OperationId.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public void Delete()
    {
        foreach (string path in Paths)
        {
            File.Delete(path);
        }
    }
}
