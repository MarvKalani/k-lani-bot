namespace KLaniBot.Actions.Handlers;

// Use case: an AI reads or writes files inside the workspace that the operator assigned to it. The module
// behind these handlers keeps every path inside that workspace; the handlers only translate arguments.
internal sealed class FileActionHandlers
{
    private readonly HandlerServices _services;

    public FileActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.FsListDirectory, ListDirectory);
        add(Tool.FsReadFile, ReadFile);
        add(Tool.FsWriteFile, WriteFile);
        add(Tool.FsSearchFiles, SearchFiles);
        add(Tool.FsGetMetadata, GetMetadata);
    }

    private object ListDirectory(ActionCall call)
    {
        string? path = call.Arguments["path"]?.ToString();
        return _services.Files.ListDirectory(path);
    }

    private object ReadFile(ActionCall call)
    {
        string? path = call.Arguments["path"]?.ToString();
        int? offset = call.Arguments["offset"]?.GetValue<int?>();
        int? length = call.Arguments["length"]?.GetValue<int?>();
        return _services.Files.ReadFile(path, offset, length);
    }

    private object WriteFile(ActionCall call)
    {
        string? path = call.Arguments["path"]?.ToString();
        string? content = call.Arguments["content"]?.ToString();
        bool overwrite = call.Arguments["overwrite"]?.GetValue<bool?>() ?? false;
        return _services.Files.WriteFile(path, content, overwrite);
    }

    private object SearchFiles(ActionCall call)
    {
        string? path = call.Arguments["path"]?.ToString();
        string? pattern = call.Arguments["pattern"]?.ToString();
        int maxResults = call.Arguments["max_results"]?.GetValue<int?>() ?? 50;
        return _services.Files.SearchFiles(path, pattern, maxResults);
    }

    private object GetMetadata(ActionCall call)
    {
        string? path = call.Arguments["path"]?.ToString();
        return _services.Files.GetMetadata(path);
    }
}
