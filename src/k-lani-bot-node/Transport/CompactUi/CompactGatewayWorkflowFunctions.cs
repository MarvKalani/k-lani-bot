namespace KLaniBot.Transport.CompactUi;

// Workflow function lookups read one active stored recipe without requiring a writable learning root or exposing its siblings.
public sealed class CompactGatewayWorkflowFunctions(CompactGatewayPrograms programs) : ICompactWorkflowFunctionSource
{
    public bool TryLoad(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
        out CompactFunctionAttempt<CompactLoadedFunction> function)
    {
        try
        {
            function = new(programs.Get(application.Identity, reference.Name));

            return true;
        }
        catch (Exception exception)
        {
            function = new(null, CompactFunctionError.InvalidDefinition, exception.Message);
            return false;
        }
    }

    public string? ProcessName(CompactProgramIdentity application)
    {
        return new GatewayProgramCatalog(programs.LibraryRoot).Read()
            .SingleOrDefault(entry => entry.Identity == application || entry.Generated?.Application == application)?.ProcessName;
    }

    public bool TryResolve(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
        out CompactWorkflowFunctionContract? function)
    {
        function = null;
        if (!TryLoad(reference, application, out var loaded))
        {
            return false;
        }
        function = CompactWorkflowFunctionCatalog.Contract(loaded.Value!);

        return true;
    }
}
