using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Transport.CompactUi;

// A selected application stores structural observations atomically in its private directory; live contents are never accepted here.
public sealed class CompactObservedFormStore
{
    public const string FileName = "observed-forms.json";
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private readonly string _directory;
    private readonly CompactProgramIdentity _application;
    private string? _digest;
    public string FilePath
    {
        get;
    }
    public IReadOnlyDictionary<string, CompactObservedForm> Forms
    {
        get; private set;
    }

    private CompactObservedFormStore(string directory, CompactProgramIdentity application,
        IReadOnlyDictionary<string, CompactObservedForm> forms, string? digest)
    {
        _directory = directory;
        _application = application;
        _digest = digest;
        FilePath = Path.Combine(directory, FileName);
        Forms = forms;
    }

    public static bool TryOpen(string directory, CompactProgramIdentity application,
        out CompactDiscoveryAttempt<CompactObservedFormStore> attempt)
    {
        attempt = new(null, CompactDiscoveryError.InvalidPath);
        try
        {
            if (!Path.IsPathFullyQualified(directory) || Path.GetFullPath(directory) == Path.GetPathRoot(directory))
            {
                return false;
            }
            string full = Path.GetFullPath(directory);
            string file = Path.Combine(full, FileName);
            CartridgeManifest.RejectLinks(file);
            if (string.IsNullOrWhiteSpace(application.Key) || string.IsNullOrWhiteSpace(application.Build)
                || application.Build.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                attempt = new(null, CompactDiscoveryError.InvalidDocument);
                return false;
            }
            byte[]? content = ReadBytes(file);
            IReadOnlyDictionary<string, CompactObservedForm> forms = new Dictionary<string, CompactObservedForm>(StringComparer.Ordinal);
            if (content != null)
            {
                if (!TryDocument(content, application, out var parsed))
                {
                    attempt = new(null, parsed.Error, parsed.Detail);
                    return false;
                }
                forms = parsed.Value!;
            }
            attempt = new(new(full, application, forms, Digest(content)));

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactDiscoveryError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    public bool TryReplace(IReadOnlyDictionary<string, CompactObservedForm> forms,
        out CompactDiscoveryAttempt<CompactObservedFormStore> attempt)
    {
        attempt = new(null, CompactDiscoveryError.StorageUnavailable);
        try
        {
            byte[] content = Encoding.UTF8.GetBytes(CompactJsonText.Serialize(CompactDiscoveryWire.Document(_application, forms)) + "\n");
            if (!TryDocument(content, _application, out var validated))
            {
                attempt = new(null, validated.Error, validated.Detail);
                return false;
            }
            CartridgeManifest.RejectLinks(FilePath);
            CartridgeManifest.RejectLinks(FilePath + ".lock");
            PrivateFile.CreateDirectory(_directory);
            using FileStream ownership = PrivateFile.OpenWrite(FilePath + ".lock", FileMode.OpenOrCreate);
            if (Digest(ReadBytes(FilePath)) != _digest)
            {
                attempt = new(null, CompactDiscoveryError.StorageChanged);
                return false;
            }
            PrivateFile.WriteAllBytesAtomically(FilePath, content);
            _digest = Digest(content);
            Forms = validated.Value!;
            attempt = new(this);

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactDiscoveryError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    private static bool TryDocument(byte[] content, CompactProgramIdentity application,
        out CompactDiscoveryAttempt<IReadOnlyDictionary<string, CompactObservedForm>> attempt)
    {
        if (content.Length > MaximumDocumentBytes)
        {
            attempt = new(null, CompactDiscoveryError.DocumentBoundExceeded);
            return false;
        }

        try
        {
            return CompactDiscoveryDocumentCodec.TryRead(JsonNode.Parse(content), application, out attempt);
        }
        catch (System.Text.Json.JsonException)
        {
            attempt = new(null, CompactDiscoveryError.InvalidDocument);
            return false;
        }
    }

    // Throws IOException for a non-file or oversized existing document, so callers can report a bounded storage failure.
    private static byte[]? ReadBytes(string file)
    {
        if (Directory.Exists(file))
        {
            throw new IOException("The discovery document must be a regular file.");
        }
        if (!File.Exists(file))
        {
            return null;
        }
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumDocumentBytes)
        {
            throw new IOException("Observed discovery document exceeds its storage bound.");
        }
        byte[] content = new byte[(int)stream.Length];
        stream.ReadExactly(content);
        if (stream.ReadByte() != -1)
        {
            throw new IOException("Observed discovery document changed while reading.");
        }

        return content;
    }

    private static string? Digest(byte[]? content)
    {
        return content == null ? null : Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
