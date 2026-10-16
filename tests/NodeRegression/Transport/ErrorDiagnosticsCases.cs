using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Advisory;
using KLaniBot.Guard;
using KLaniBot.Startup;
using KLaniBot.Transport;
using KLaniBot.Visibility;

// Caught exceptions used to vanish without a type: the gateway answered "Gateway failure" and the
// indicator gate could not say whether the screen or the session failed. These cases check that the
// cause is named, and that cleaning the message keeps secrets and foreign paths out of it.
internal static class ErrorDiagnosticsCases
{
    // Built at run time: a literal token would itself be what the repository's secret scan rejects.
    private static readonly string Secret = string.Concat(Enumerable.Repeat("q7Z", 15));

    public static async Task<int> RunAsync(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Error diagnostics: " + name);
            }
            passed++;
        }
        CleaningRemovesSecretsAndForeignPaths(Check, temporaryRoot);
        await GatewayNamesTheFailureAndKeepsServing(Check, temporaryRoot);
        await FailedToolCallShowsItsReasonAsText(Check, WriteGatewayRegistry(temporaryRoot));
        await CartridgeCatalogStaysCompact(Check, WriteGatewayRegistry(temporaryRoot));
        IndicatorGateSeparatesRenderFromSession(Check);
        return passed;
    }

    private static void CleaningRemovesSecretsAndForeignPaths(Action<bool, string> check, string temporaryRoot)
    {
        string state = Path.Combine(temporaryRoot, "diagnostics-state");
        string operationId = Guid.CreateVersion7().ToString();
        string message = "Bearer " + Secret + " for /srv/foreign-service/keys/id_ed25519 and " + Secret
            + " near " + state + "/audit.jsonl, operation " + operationId + ".";
        string cleaned = Diagnostics.Describe(new InvalidOperationException(message), state);
        check(cleaned.StartsWith("InvalidOperationException: ", StringComparison.Ordinal), "type comes first");
        check(!cleaned.Contains(Secret, StringComparison.Ordinal), "token values are removed");
        check(!cleaned.Contains("/srv/foreign-service", StringComparison.Ordinal), "foreign paths are removed");
        check(cleaned.Contains(state + "/audit.jsonl", StringComparison.Ordinal), "state paths stay readable");
        check(cleaned.Contains(operationId, StringComparison.Ordinal), "operation ids stay readable");
        string windows = Diagnostics.Clean(@"Access to D:\Fremd\someone\secret.txt denied.");
        check(!windows.Contains("someone", StringComparison.Ordinal), "Windows paths are removed");
        string longMessage = Diagnostics.Clean(string.Join(" ", Enumerable.Repeat("word", 200)));
        check(longMessage.Length == 241, "messages are bounded");
    }

    private static async Task GatewayNamesTheFailureAndKeepsServing(Action<bool, string> check, string temporaryRoot)
    {
        string registry = WriteGatewayRegistry(temporaryRoot);
        var frames = new StringBuilder();
        frames.AppendLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
        frames.AppendLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        frames.AppendLine("null");
        frames.AppendLine("""{"jsonrpc":"2.0","jsonrpc":"2.0","method":"ping","id":9}""");
        string arguments = """{"name":"evaluate_trace","arguments":{"state":{},"questions":{}}}""";
        frames.AppendLine("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":""" + arguments + "}");
        frames.AppendLine("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames.ToString()));
        var output = new StringWriter();
        TextWriter originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            using var gateway = new McpGateway(registry, null, new FailingAdvisor());
            await gateway.RunAsync(input, output, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }
        JsonObject[] answers = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToArray();
        JsonObject failed = answers.Single(answer => answer["id"]?.ToString() == "2");
        check(failed["error"]?["code"]?.GetValue<int>() == -32603, "handler failure is an internal error");
        check(failed["error"]?["data"]?["diagnostic"]?.ToString() == "InvalidOperationException", "the type is reported");
        check(!failed.ToJsonString().Contains(Secret, StringComparison.Ordinal), "the message stays on the node side");
        check(answers.Any(answer => answer["id"]?.ToString() == "3" && answer["result"] != null), "later requests are still served");
        int invalidFrames = answers.Count(answer => answer["error"]?["code"]?.GetValue<int>() == -32700);
        check(invalidFrames == 2, "null and duplicate-key frames are answered, not fatal");
        string diagnostics = capturedError.ToString();
        bool causeLogged = diagnostics.Contains("Gateway failure: InvalidOperationException", StringComparison.Ordinal);
        check(causeLogged, "the cause goes to standard error");
        check(!diagnostics.Contains(Secret, StringComparison.Ordinal), "standard error is cleaned");
    }

    private static async Task FailedToolCallShowsItsReasonAsText(Action<bool, string> check, string registry)
    {
        string frames = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_machine_capabilities","arguments":{"node":"fixture"}}}
            """ + "\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames));
        var output = new StringWriter();
        TextWriter originalError = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            using var gateway = new McpGateway(registry, null);
            await gateway.RunAsync(input, output, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }
        JsonObject[] answers = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        JsonObject result = answers.Single(answer => answer["id"]?.ToString() == "2")["result"]!.AsObject();
        JsonObject structured = result["structuredContent"]!.AsObject();
        JsonObject text = JsonNode.Parse(result["content"]![0]!["text"]!.ToString())!.AsObject();
        check(result["isError"]?.GetValue<bool>() == true, "a failed call is an error result");
        check(text["success"]?.GetValue<bool>() == false, "the text content is the failed result");
        check(structured["code"] != null && text["code"]?.ToString() == structured["code"]!.ToString(), "the text carries the code");
        check(structured["error"] != null && text["error"]?.ToString() == structured["error"]!.ToString(), "the text carries the error");
    }

    private static async Task CartridgeCatalogStaysCompact(Action<bool, string> check, string registry)
    {
        string bundle = Path.Combine(RepositoryLocator.Root(),
            "cartridges/programs/outlook-classic/16.0.14334.20918/cartridge.json");
        string frames = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"cartridge_browse","arguments":{"path":"Use/Mail/Read"}}}
            """ + "\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames));
        var output = new StringWriter();
        using var gateway = new McpGateway(registry, null, cartridgeManifest: bundle);
        await gateway.RunAsync(input, output, CancellationToken.None);
        JsonObject[] responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        JsonArray catalog = responses.Single(frame => frame["id"]?.GetValue<int>() == 2)["result"]!["tools"]!.AsArray();
        check(catalog.Count == 3 && catalog.Select(tool => tool!["name"]!.ToString())
            .ToHashSet().SetEquals(new[] { "cartridge_info", "cartridge_browse", "cartridge_call" }),
            "the real MCP session advertises exactly the three compact cartridge routing tools");
        check(responses.Single(frame => frame["id"]?.GetValue<int>() == 3)["error"] != null,
            "an application topic cannot be loaded before the program's Info binding");
    }

    private static string WriteGatewayRegistry(string temporaryRoot)
    {
        string root = Path.Combine(temporaryRoot, "gateway-diagnostics");
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=gateway fixture CA", key, HashAlgorithmName.SHA256);
        using X509Certificate2 authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] rootDer = authority.RawData;
        var entry = new JsonObject
        {
            ["url"] = "https://127.0.0.1:9",
            ["root_der_base64"] = Convert.ToBase64String(rootDer),
            ["root_sha256"] = Convert.ToHexStringLower(SHA256.HashData(rootDer)),
            ["token_file"] = Path.Combine(root, "credential")
        };
        var document = new JsonObject
        {
            ["version"] = 1,
            ["nodes"] = new JsonObject { ["fixture"] = entry }
        };
        string path = Path.Combine(root, "nodes.json");
        File.WriteAllText(path, document.ToJsonString());
        return path;
    }

    private static void IndicatorGateSeparatesRenderFromSession(Action<bool, string> check)
    {
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        TextWriter originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        JsonObject? renderRejection;
        JsonObject? sessionRejection;
        try
        {
            using var brokenScreen = new AccessVisibility(new VisibilityOptions(), new ThrowingDisplay(), _ => { });
            renderRejection = brokenScreen.Begin(Tool.TakeScreenshot, caller).Rejection;
            var journalFails = new AccessVisibility(new VisibilityOptions(), new SilentDisplay(), _ => throw new IOException("disk full"));
            using (journalFails)
            {
                sessionRejection = journalFails.Begin(Tool.TakeScreenshot, caller).Rejection;
            }
        }
        finally
        {
            Console.SetError(originalError);
        }
        check(renderRejection?["code"]?.ToString() == "indicator_unavailable", "render failure refuses the action");
        check(renderRejection?["error"]?.ToString().Contains("ExternalException") == true, "render failure names the renderer's type");
        check(sessionRejection?["code"]?.ToString() == "indicator_unavailable", "journal failure refuses the action");
        check(sessionRejection?["error"]?.ToString().Contains("IOException") == true, "journal failure names its type");
        string diagnostics = capturedError.ToString();
        check(diagnostics.Contains("Activity indicator could not be drawn", StringComparison.Ordinal), "render failure is logged as such");
        check(diagnostics.Contains("Visible session unavailable", StringComparison.Ordinal), "session failure is logged as such");
    }

    // An indicator that fails once a session is shown, to check how a display failure is reported.
    private sealed class ThrowingDisplay : IVisibilityDisplay
    {
        // The first frame (no session yet) is drawn, so the gate starts; the session's frame fails.
        public void Render(VisibilityState state)
        {
            if (state.Connections > 0)
            {
                throw new System.Runtime.InteropServices.ExternalException("compositor went away");
            }
        }

        public void Dispose()
        {
        }
    }

    // A working indicator that draws nothing, for cases that are about errors elsewhere.
    private sealed class SilentDisplay : IVisibilityDisplay
    {
        public void Render(VisibilityState state)
        {
        }

        public void Dispose()
        {
        }
    }

    // An advisory provider whose failure message carries a secret and a foreign path, which the diagnostics must not pass on.
    private sealed class FailingAdvisor : IAdvisoryEvaluator
    {
        public string ProviderId => "failing-fixture";

        public Task<AdvisoryResult> EvaluateAsync(JsonNode state, JsonObject questions, CancellationToken cancellation = default)
        {
            throw new InvalidOperationException("Provider leaked " + Secret + " from /srv/foreign-service/advisor.json");
        }

        public void Dispose()
        {
        }
    }
}
