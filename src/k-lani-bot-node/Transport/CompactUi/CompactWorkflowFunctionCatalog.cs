namespace KLaniBot.Transport.CompactUi;

// One configured application variant/build maps to its actual process name and single active generated-function library.
public sealed record CompactWorkflowProgram(string ProcessName, CompactFunctionLibrary Library);

// Workflow adapters reload only the selected host function and resolve its metadata without exposing unrelated schemas to the model.
public interface ICompactWorkflowFunctionSource : ICompactWorkflowFunctions
{
    bool TryLoad(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
        out CompactFunctionAttempt<CompactLoadedFunction> function);
    string? ProcessName(CompactProgramIdentity application);
}

// Generated-function catalogs preserve one entry per application/build and reload selected definitions before every reuse check.
public sealed class CompactWorkflowFunctionCatalog(IReadOnlyDictionary<CompactProgramIdentity, CompactWorkflowProgram> programs)
    : ICompactWorkflowFunctionSource
{
    public bool TryLoad(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
        out CompactFunctionAttempt<CompactLoadedFunction> function)
    {
        function = new(null, CompactFunctionError.InvalidDefinition);
        if (!programs.TryGetValue(application.Identity, out var program) || !program.Library.TryReload(out var loaded))
        {
            return false;
        }
        if (!program.Library.TryGet(reference.Name, out function))
        {
            return false;
        }
        string? validation = function.Value!.Document["validation"]?.ToString();
        if (validation == null || !validation.StartsWith("demonstrated_once", StringComparison.Ordinal))
        {
            function = new(null, CompactFunctionError.InvalidDefinition);
            return false;
        }

        return true;
    }

    public string? ProcessName(CompactProgramIdentity application)
    {
        return programs.TryGetValue(application, out var program) ? program.ProcessName : null;
    }

    public bool TryResolve(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
        out CompactWorkflowFunctionContract? function)
    {
        function = null;
        if (!TryLoad(reference, application, out var loaded))
        {
            return false;
        }
        function = Contract(loaded.Value!);

        return true;
    }

    public static CompactWorkflowFunctionContract Contract(CompactLoadedFunction loaded)
    {
        var schema = CompactCaptureWire.InputSchema(loaded.Draft)["properties"]!["parameters"]!;
        var parameters = schema["properties"]!.AsObject().ToDictionary(entry => entry.Key, entry => CompactWorkflowValueKind.String,
            StringComparer.Ordinal);
        var required = schema["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        var outputs = loaded.Draft.Outputs.ToDictionary(output => output.Name, output => CompactWorkflowValueKind.String,
            StringComparer.Ordinal);
        return new(true, true, parameters, required, outputs);
    }
}
