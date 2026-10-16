using System.Reflection;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport.CompactUi;

// Compact program access reads one active generated extension per observed variant/build; absent learning roots never
// authorize writes or recovery.
public sealed class CompactGatewayPrograms(CompactStorageRoots roots)
{
    public string LibraryRoot => roots.Library;
    // Throws CompactNodeException for unknown builds, unsafe files, invalid/future versions or inaccessible program state.
    public IReadOnlyList<CompactLoadedFunction> Read(CompactProgramIdentity application)
    {
        return ReadDocument(application)?.Functions ?? [];
    }

    // Throws CompactNodeException for unknown builds, unsafe files, invalid/future versions or inaccessible program state.
    public CompactFunctionDocument? ReadDocument(CompactProgramIdentity application)
    {
        var selected = new GatewayProgramCatalog(roots.Library).Generated(application);
        string file = selected?.File ?? Path.Combine(Directories(application).Library, "generated-ui.json");
        if (!CompactRevisionFiles.TryRead(file, out var source))
        {
            throw new CompactNodeException(CompactFunctionNames.Error(source.Error!.Value, source.Detail));
        }
        if (source.Value == null)
        {
            return null;
        }
        if (!CompactFunctionDocumentCodec.TryRead(JsonNode.Parse(source.Value), selected?.Application ?? application, out var read))
        {
            throw new CompactNodeException(CompactFunctionNames.Error(read.Error!.Value));
        }

        return read.Value;
    }

    // Throws CompactNodeException for an unknown/ambiguous function or an unverified label in the loaded document.
    public CompactLoadedFunction Get(CompactProgramIdentity application, string name)
    {
        var functions = Read(application).Where(function => function.Draft.Name == name).ToArray();
        if (functions.Length != 1 || !IsDemonstrated(functions[0]))
        {
            throw new CompactNodeException("Selected generated function is absent or unverified.");
        }

        return functions[0];
    }

    // Only a function demonstrated once is callable, so every listing of stored functions applies the same test as Get.
    public static bool IsDemonstrated(CompactLoadedFunction function)
    {
        return function.Document["validation"]?.ToString()?.StartsWith("demonstrated_once", StringComparison.Ordinal) == true;
    }

    // Throws CompactNodeException when persistence was not configured or the active library cannot be safely opened.
    public CompactFunctionLibrary Writable(CompactProgramIdentity application)
    {
        var directories = Directories(application);
        if (directories.Learning == null)
        {
            throw new CompactNodeException("A private learning root is required to save functions.");
        }
        string file = Path.Combine(directories.Library, "generated-ui.json");
        var selected = new GatewayProgramCatalog(roots.Library).Generated(application);
        file = selected?.File ?? file;
        if (!CompactFunctionLibrary.TryOpen(file, Path.Combine(directories.Learning, "recovery"),
            selected?.Application ?? application, out var library))
        {
            throw new CompactNodeException(CompactFunctionNames.Error(library.Error!.Value, library.Detail));
        }

        return library.Value!;
    }

    // Throws CompactNodeException for unknown app identity or unsafe roots, without incorporating identity strings into a path.
    public CompactProgramDirectories Directories(CompactProgramIdentity application)
    {
        if (!CompactProgramStorage.TryDirectories(roots, application, out var paths))
        {
            throw new CompactNodeException("Known application identity and safe separate roots are required.");
        }

        return paths.Value!;
    }

    // Throws CompactNodeException for conflicting variant mappings; preserved legacy keys are never silently renamed in
    // compatibility evidence.
    public CompactProgramIdentity BoundIdentity(CompactNativeWindow window)
    {
        var observed = CompactGatewayLearning.Identity(window);
        var entry = new GatewayProgramCatalog(roots.Library).Generated(observed);

        return entry?.Application ?? observed;
    }

    public static CompactContractDigest Implementation()
    {
        string? native = typeof(CompactGatewayPrograms).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "NativeImplementationSha256")?.Value;

        return CompactContractStructure.Digest(native, CompactGatewayTools.Descriptors(), CompactGatewayFunctionTools.All);
    }
}
