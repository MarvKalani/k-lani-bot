using System.Diagnostics;
using System.Text.Json.Nodes;

// Development checks execute the JS reference on the host, while the published bot itself never starts another runtime.
internal static class SharedParityCases
{
    private static readonly TimeSpan ReferenceDeadline = TimeSpan.FromSeconds(30);

    public static async Task RunAsync()
    {
        string repository = FindRepository();
        string descriptors = Path.Combine(repository, "tests/CompactUiRegression/fixtures/compact-descriptors.json");
        RequireSame(repository, descriptors, CompactDescriptorParityCases.Run());
        string pages = Path.Combine(repository, "tests/CompactUiRegression/fixtures/result-pages.json");
        string formats = Path.Combine(repository, "tests/CompactUiRegression/fixtures/result-format.json");
        RequireSame(repository, pages, ResultParityCases.Run(pages));
        RequireSame(repository, formats, ResultFormatParityCases.Run(formats));
        string views = Path.Combine(repository, "tests/CompactUiRegression/fixtures/view-pages.json");
        RequireSame(repository, views, ViewParityCases.Run(views));
        string projection = Path.Combine(repository, "tests/CompactUiRegression/fixtures/projection.json");
        RequireSame(repository, projection, ProjectionParityCases.Run(projection));
        string forms = Path.Combine(repository, "tests/CompactUiRegression/fixtures/forms.json");
        RequireSame(repository, forms, await FormParityCases.RunAsync(forms));
        string arguments = Path.Combine(repository, "tests/CompactUiRegression/fixtures/form-arguments.json");
        RequireSame(repository, arguments, FormArgumentParityCases.Run(arguments));
        string discovery = Path.Combine(repository, "tests/CompactUiRegression/fixtures/discovery.json");
        RequireSame(repository, discovery, DiscoveryParityCases.Run(discovery));
        string capture = Path.Combine(repository, "tests/CompactUiRegression/fixtures/capture.json");
        RequireSame(repository, capture, CaptureParityCases.Run(capture));
        string outputs = Path.Combine(repository, "tests/CompactUiRegression/fixtures/output-verification.json");
        RequireSame(repository, outputs, OutputVerificationParityCases.Run(outputs));
        string compatibility = Path.Combine(repository, "tests/CompactUiRegression/fixtures/compatibility.json");
        RequireSame(repository, compatibility, CompatibilityParityCases.Run(compatibility));
        string nativeApi = Path.Combine(repository, "tests/CompactUiRegression/fixtures/native-api.json");
        RequireSame(repository, nativeApi, NativeApiParityCases.Run(nativeApi));
        string automatic = Path.Combine(repository, "tests/CompactUiRegression/fixtures/automatic-capture.json");
        RequireSame(repository, automatic, AutomaticCaptureParityCases.Run(automatic));
        string documents = Path.Combine(repository, "tests/CompactUiRegression/fixtures/function-documents.json");
        RequireSame(repository, documents, FunctionDocumentParityCases.Run(documents));
        string receipts = Path.Combine(repository, "tests/CompactUiRegression/fixtures/receipts.json");
        RequireSame(repository, receipts, ReceiptParityCases.Run(receipts));
        string workflows = Path.Combine(repository, "tests/CompactUiRegression/fixtures/workflow-definitions.json");
        RequireSame(repository, workflows, WorkflowDefinitionParityCases.Run(workflows));
        string jobs = Path.Combine(repository, "tests/CompactUiRegression/fixtures/workflow-jobs.json");
        RequireSame(repository, jobs, WorkflowJobParityCases.Run(jobs));
        string engine = Path.Combine(repository, "tests/CompactUiRegression/fixtures/workflow-engine.json");
        RequireSame(repository, engine, await WorkflowEngineParityCases.RunAsync(engine));
        RequireGatewayRules(repository);
    }

    // Window selection, learning's window proof, the stored-function listing and the names of stored steps match in both gateways.
    private static void RequireGatewayRules(string repository)
    {
        string listing = Path.Combine(repository, "tests/CompactUiRegression/fixtures/stored-listing.json");
        RequireSame(repository, listing, StoredListingParityCases.Run(listing));
        string labels = Path.Combine(repository, "tests/CompactUiRegression/fixtures/step-labels.json");
        RequireSame(repository, labels, StepLabelParityCases.Run(labels));
        string windowState = Path.Combine(repository, "tests/CompactUiRegression/fixtures/window-state.json");
        RequireSame(repository, windowState, WindowStateCases.Run(windowState));
        string windowDiff = Path.Combine(repository, "tests/CompactUiRegression/fixtures/window-diff.json");
        RequireSame(repository, windowDiff, WindowDiffCases.Run(windowDiff));
        RequireWindowRules(repository);
    }

    internal static string FindRepository()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "modules/desktop-ui-mcp/result-store.mjs")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Run compact parity checks from this repository.");
    }

    // Window selection and learning's window proof answer the same requests in the compact gateway and in the JS twin.
    private static void RequireWindowRules(string repository)
    {
        string windows = Path.Combine(repository, "tests/CompactUiRegression/fixtures/window-selection.json");
        RequireSame(repository, windows, NativeAdapterCases.WindowSelectionParity(windows));
        string instance = Path.Combine(repository, "tests/CompactUiRegression/fixtures/window-instance.json");
        RequireSame(repository, instance, NativeAdapterCases.WindowInstanceParity(instance));
    }

    internal static void RequireSame(string repository, string fixture, JsonNode native)
    {
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repository
        };
        start.ArgumentList.Add(Path.Combine(repository, "tests/CompactUiRegression/parity-reference.mjs"));
        start.ArgumentList.Add(fixture);
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit((int)ReferenceDeadline.TotalMilliseconds))
        {
            child.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Host parity reference exceeded its deadline.");
        }
        if (child.ExitCode != 0)
        {
            throw new InvalidOperationException("Host parity reference failed: " + error.GetAwaiter().GetResult());
        }
        JsonNode reference = JsonNode.Parse(output.GetAwaiter().GetResult())!;
        if (!JsonNode.DeepEquals(native, reference))
        {
            throw new InvalidOperationException("Compact semantic parity differs for " + Path.GetFileName(fixture));
        }
    }
}
