using KLaniBot.Transport.CompactUi;
using System.Text.Json.Nodes;

// The native compact regression checks deterministic semantics without contacting applications or model providers.
internal static class Program
{
    // A failed check ends the run with its full message and exit code 1 instead of aborting the process with a core dump.
    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            return await RunWithArgumentsAsync(arguments);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Regression failed: " + exception);

            return 1;
        }
    }

    private static async Task<int> RunWithArgumentsAsync(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "--package-version")
        {
            return PackageVersion(arguments[1]);
        }
        if (arguments.Length == 3 && arguments[0] == "--revision-crash")
        {
            FunctionLibraryCases.CrashChild(arguments[1], arguments[2]);
            return 1;
        }
        JsonNode? parity = arguments.Length == 2 ? await ParityAsync(arguments[0], arguments[1]) : null;
        if (parity != null)
        {
            Console.WriteLine(parity.ToJsonString());
            return 0;
        }
        await RunAsync();

        return 0;
    }

    private static int PackageVersion(string executable)
    {
        var reader = new KLaniBot.Platforms.Linux.LinuxExecutableVersion();
        bool available = reader.TryRead(executable, out var build);
        Console.WriteLine(new JsonObject
        {
            ["available"] = available,
            ["package"] = build?.Package,
            ["version"] = build?.Version
        }.ToJsonString());

        return available ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        LinuxExecutableVersionCases.Run();
        ResultStoreCases.Run();
        ViewStoreCases.Run();
        TreeCases.Run();
        WindowStateCases.Check();
        WindowDiffCases.Check();
        CommonUiRowCases.Run();
        RoleFamilyCases.Run();
        await FormRuntimeCases.RunAsync();
        DiscoveryStoreCases.Run();
        ProgramStorageCases.Run();
        CaptureCompilerCases.Run();
        AutomaticCaptureCases.Run();
        FunctionLibraryCases.Run();
        AcceptanceStoreCases.Run();
        WorkflowContractCases.Run();
        await WorkflowJobStoreCases.RunAsync();
        await WorkflowEngineCases.RunAsync();
        await NativeAdapterCases.RunAsync();
        await CompactGatewayCases.RunAsync();
        await CompactGatewayFormCases.RunAsync();
        await CompactGatewayAutomaticCases.RunAsync();
        await CompactGatewayCompatibilityCases.RunAsync();
        await CompactGatewayWorkflowCases.RunAsync();
        await CompactGatewayReadWorkflowCases.RunAsync();
        await CompactGatewayListingCases.RunAsync();
        await BuildDriftCases.RunAsync();
        await InstalledCartridgeCases.RunAsync();
        await CompactGatewayObservationCases.RunAsync();
        await CompactGatewayApplyCases.RunAsync();
        await CompactGatewayInputCases.RunAsync();
        await CompactGatewayCallCases.RunAsync();
        await ProgramCatalogCases.RunAsync();
        CompactContractDigestCases.Run();
        await SharedParityCases.RunAsync();
        Console.WriteLine("Compact result/view/capture/library/workflow checks and 624 shared JS/native parity cases passed.");
    }

    private static async Task<JsonNode?> ParityAsync(string option, string file)
    {
        return option switch
        {
            "--result-parity" => ResultParityCases.Run(file),
            "--format-parity" => ResultFormatParityCases.Run(file),
            "--view-parity" => ViewParityCases.Run(file),
            "--projection-parity" => ProjectionParityCases.Run(file),
            "--role-family-parity" => RoleFamilyCases.Observe(file),
            "--window-state-parity" => WindowStateCases.Run(file),
            "--window-diff-parity" => WindowDiffCases.Run(file),
            "--form-parity" => await FormParityCases.RunAsync(file),
            "--discovery-parity" => DiscoveryParityCases.Run(file),
            "--capture-parity" => CaptureParityCases.Run(file),
            "--compatibility-parity" => CompatibilityParityCases.Run(file),
            "--workflow-definition-parity" => WorkflowDefinitionParityCases.Run(file),
            "--workflow-engine-parity" => await WorkflowEngineParityCases.RunAsync(file),
            _ => null
        };
    }
}
