using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Common;
using KLaniBot.Transport;

namespace KLaniBot.Advisory;

// Trusted evaluator runs outside the actuator workspace. It owns the oracle,
// computes real artifact evidence and records model predictions as a separate channel.
public static class EvaluationRunner
{
    private const int MaximumPredicates = 100;
    private const long MaximumArtifactBytes = 64 * 1024 * 1024;
    private const int MaximumComparedTextBytes = 1024 * 1024;

    // Throws InvalidDataException for an unknown specification format, UnauthorizedAccessException when the specification lies in the
    // workspace and ArgumentException for an unregistered node.
    public static async Task<JsonObject> RunAsync(
        string specificationPath,
        string registryPath,
        IAdvisoryEvaluator? classifier = null,
        CancellationToken token = default)
    {
        var specification = JsonNode.Parse(File.ReadAllText(specificationPath))!.AsObject();
        if (specification["version"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Unknown evaluation format.");
        string workspace = OracleWorkspace(specification, specificationPath);
        var registry = JsonNode.Parse(File.ReadAllText(registryPath))!;
        string node = specification["node"]!.ToString();
        var registered = registry["nodes"]?[node] ?? throw new ArgumentException("Unknown registered evaluation node.");

        using var client = new NodeTransportClient(node, Path.GetFullPath(registryPath), registered["token_env"]?.ToString());
        var recipe = specification["recipe"]!.AsObject();
        string operation = Guid.CreateVersion7().ToString();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var execution = await client.CallAsync(Tool.RunMacro, MacroArguments(recipe, operation), token);
        var evidence = VerifyArtifacts(workspace, specification["oracles"]!.AsArray());
        bool hardPass = execution["success"]?.GetValue<bool>() == true && evidence.All(item => item!["passed"]!.GetValue<bool>());
        var report = new JsonObject
        {
            ["version"] = 1,
            ["evaluation_id"] = Guid.CreateVersion7().ToString(),
            ["operation_id"] = operation,
            ["case_sha256"] = CanonicalJson.Hash(specification),
            ["recipe_sha256"] = recipe["sha256"]!.DeepClone(),
            ["node"] = node,
            ["hard_pass"] = hardPass,
            ["duration_ms"] = watch.ElapsedMilliseconds,
            ["evidence"] = evidence,
            ["execution_outcome"] = execution["outcome"]?.DeepClone(),
            ["receipt"] = execution["receipt"]?.DeepClone()
        };
        if (classifier != null && specification["soft_questions"] is JsonObject questions)
            report["classifier"] = (await classifier.EvaluateAsync(Observable(specification, report), questions, token)).ToJson();
        return report;
    }

    // The agent must not be able to read or rewrite the oracle, so the specification lies outside the artifact root.
    // Throws UnauthorizedAccessException when the specification lies inside the workspace.
    private static string OracleWorkspace(JsonObject specification, string specificationPath)
    {
        string workspace = Path.GetFullPath(specification["artifact_root"]!.ToString());
        string spec = Path.GetFullPath(specificationPath);

        if (spec == workspace || spec.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The oracle specification must be outside the agent workspace.");
        return workspace;
    }

    private static JsonObject MacroArguments(JsonObject recipe, string operation)
    {
        return new JsonObject
        {
            ["name"] = recipe["name"]!.DeepClone(),
            ["sha256"] = recipe["sha256"]!.DeepClone(),
            ["params"] = recipe["params"]?.DeepClone() ?? new JsonObject(),
            ["request_id"] = operation
        };
    }

    // Never disclose expected artifact contents, rubric labels from a held-out oracle,
    // credentials, or private specification paths to the decision model.
    private static JsonObject Observable(JsonObject specification, JsonObject report)
    {
        return new JsonObject
        {
            ["goal"] = specification["goal"]?.DeepClone(),
            ["execution_outcome"] = report["execution_outcome"]?.DeepClone(),
            ["hard_pass"] = report["hard_pass"]?.DeepClone()
        };
    }

    // Throws ArgumentException for missing or malformed predicates, UnauthorizedAccessException for a path outside the workspace or through
    // a link, and InvalidDataException for an artifact beyond its size limit.
    public static JsonArray VerifyArtifacts(string workspace, JsonArray predicates)
    {
        if (predicates.Count is < 1 or > MaximumPredicates)
            throw new ArgumentException("Independent artifact predicates are required.");

        var output = new JsonArray();
        foreach (var predicate in predicates.OfType<JsonObject>())
            output.Add(CheckPredicate(workspace, predicate));
        if (output.Count != predicates.Count)
            throw new ArgumentException("Every oracle must be a predicate object.");
        return output;
    }

    // Throws ArgumentException for an unknown property or a predicate without condition, UnauthorizedAccessException for a path
    // outside the workspace or through a link, and InvalidDataException for an artifact beyond its size limit.
    private static JsonObject CheckPredicate(string workspace, JsonObject predicate)
    {
        if (predicate.Any(p => p.Key is not ("path" or "exists" or "sha256" or "text_equals")))
            throw new ArgumentException("Unknown oracle property.");
        string relative = predicate["path"]!.ToString();
        string full = ArtifactPath(workspace, relative);

        bool passed;
        string? actualHash = null;
        if (File.Exists(full))
        {
            byte[] bytes = ReadArtifact(full);
            actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            passed = MatchesPresentArtifact(predicate, bytes, actualHash);
        }
        else
            passed = predicate["exists"]?.GetValue<bool>() == false;
        if (predicate["sha256"] == null && predicate["text_equals"] == null && predicate["exists"] == null)
            throw new ArgumentException("An explicit artifact condition is required.");
        return new JsonObject { ["artifact"] = relative, ["passed"] = passed, ["actual_sha256"] = actualHash };
    }

    // Throws UnauthorizedAccessException for a path outside the workspace or through a link.
    private static string ArtifactPath(string workspace, string relative)
    {
        string full = Path.GetFullPath(relative, workspace);
        if (!full.StartsWith(Path.GetFullPath(workspace) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Artifact path escapes the evaluation workspace.");

        for (string? component = full; component != null; component = Path.GetDirectoryName(component))
        {
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Oracle does not follow links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    // Read once into memory, so the hash and the text comparison see the same bytes even if the file changes meanwhile.
    // Throws InvalidDataException when the artifact is or grows beyond the size limit.
    private static byte[] ReadArtifact(string full)
    {
        if (new FileInfo(full).Length > MaximumArtifactBytes)
            throw new InvalidDataException("Artifact exceeds the declared evaluation profile.");

        using var stream = File.OpenRead(full);
        using var snapshot = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (snapshot.Length + read > MaximumArtifactBytes)
                throw new InvalidDataException("Artifact grew beyond the evaluation profile.");
            snapshot.Write(buffer, 0, read);
        }
        return snapshot.ToArray();
    }

    private static bool MatchesPresentArtifact(JsonObject predicate, byte[] bytes, string actualHash)
    {
        bool? expectedExists = predicate["exists"]?.GetValue<bool>();
        bool hashMatches = predicate["sha256"] == null || predicate["sha256"]!.ToString() == actualHash;
        bool textMatches = predicate["text_equals"] == null
            || bytes.Length <= MaximumComparedTextBytes
            && new UTF8Encoding(false, true).GetString(bytes) == predicate["text_equals"]!.ToString();
        return expectedExists != false && hashMatches && textMatches;
    }

    public static string SignReport(JsonObject report, string privateKeyFile)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(privateKeyFile));
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(report.ToJsonString()), HashAlgorithmName.SHA256));
    }
    public static bool VerifyReport(JsonObject report, string signature, string publicKeyFile)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(publicKeyFile));
        return key.VerifyData(Encoding.UTF8.GetBytes(report.ToJsonString()), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
    }
}
