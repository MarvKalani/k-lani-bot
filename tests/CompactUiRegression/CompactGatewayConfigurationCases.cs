using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Startup;
using KLaniBot.Transport.CompactUi;

// Private startup configuration is validated before opening clients or creating an acceptance store.
internal static class CompactGatewayConfigurationCases
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-ui-config-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "library"));
        try
        {
            var roots = new CompactStorageRoots(Path.Combine(root, "library"), null);
            string file = Path.Combine(root, "private", "ui.json");
            var configuration = Document(Path.Combine(root, "evidence"));
            Write(file, configuration);
            var options = CompactGatewayOptionCodec.Read(Arguments(roots, file), NodeCommand.Mcp);
            Require(options.Configuration!.Compatibility!.Authority.Node == "fixture-node", "CLI reads trusted optional host settings");
            Require(!Directory.Exists(Path.Combine(root, "evidence")), "startup config reads do not create issuer state");
            Workflow(file, roots, configuration);
            InvalidDocuments(file, roots, configuration);
            InvalidOptions(file, roots);
            UnsafeFiles(file, roots, configuration);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static JsonObject Document(string evidence)
    {
        return new JsonObject
        {
            ["version"] = 1,
            ["compatibility"] = new JsonObject
            {
                ["evidenceRoot"] = evidence,
                ["authority"] = new JsonObject { ["node"] = "fixture-node", ["principal"] = "owner", ["connection"] = "fixture-client" }
            }
        };
    }

    private static NodeArguments Arguments(CompactStorageRoots roots, string file)
    {
        return new(["--mcp", "--ui-profile", "compact", "--nodes", "registry", "--cartridges", roots.Library, "--ui-config", file]);
    }

    private static void Write(string file, JsonObject document)
    {
        PrivateFile.CreateDirectory(Path.GetDirectoryName(file)!);
        PrivateFile.WriteAllBytes(file, System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()), replace: true);
    }

    private static void Workflow(string file, CompactStorageRoots roots, JsonObject original)
    {
        var document = original.DeepClone().AsObject();
        document["workflow"] = new JsonObject
        {
            ["stateRoot"] = Path.Combine(Path.GetDirectoryName(file)!, "jobs"),
            ["authority"] = original["compatibility"]!["authority"]!.DeepClone()
        };
        Write(file, document);
        var configured = CompactGatewayConfigurationCodec.Read(file, roots);
        Require(configured.Workflow!.Authority.Node == "fixture-node" && !Directory.Exists(configured.Workflow.StateRoot),
            "private workflow config is read without creating jobs");
        document["workflow"]!["stateRoot"] = roots.Library;
        Write(file, document);
        Refused(() => CompactGatewayConfigurationCodec.Read(file, roots));
        document["workflow"]!["stateRoot"] = "relative";
        Write(file, document);
        Refused(() => CompactGatewayConfigurationCodec.Read(file, roots));
        Write(file, original);
    }

    private static void InvalidDocuments(string file, CompactStorageRoots roots, JsonObject original)
    {
        var future = original.DeepClone().AsObject();
        future["version"] = 2;
        var extra = original.DeepClone().AsObject();
        extra["approved"] = true;
        var foreignFields = original.DeepClone().AsObject();
        foreignFields["compatibility"]!["authority"]!["token"] = true;
        var emptyAuthority = original.DeepClone().AsObject();
        emptyAuthority["compatibility"]!["authority"]!["principal"] = "";
        var overlapping = original.DeepClone().AsObject();
        overlapping["compatibility"]!["evidenceRoot"] = Path.Combine(roots.Library, "private");
        var relative = original.DeepClone().AsObject();
        relative["compatibility"]!["evidenceRoot"] = "relative";
        foreach (var document in new[] { future, extra, foreignFields, emptyAuthority, overlapping, relative })
        {
            Write(file, document);
            Refused(() => CompactGatewayConfigurationCodec.Read(file, roots));
        }
        Require(!Directory.Exists(Path.Combine(roots.Library, "private")), "invalid settings create no export-side private state");
        Write(file, original);
    }

    private static void InvalidOptions(string file, CompactStorageRoots roots)
    {
        Refused(() => CompactGatewayOptionCodec.Read(new(["--mcp", "--ui-config", file]), NodeCommand.Mcp));
        Refused(() => CompactGatewayOptionCodec.Read(new(Arguments(roots, file).Tokens.Concat(new[] { "--ui-config", file }).ToArray()),
            NodeCommand.Mcp));
        Refused(() => CompactGatewayConfigurationCodec.Read("relative.json", roots));
        Refused(() => CommandLine.Select(new(["--call", "--nodes", "registry", "--node", "test", "--ui-config", file])));
    }

    private static void UnsafeFiles(string file, CompactStorageRoots roots, JsonObject original)
    {
        string publicFile = Path.Combine(roots.Library, "ui.json");
        Write(publicFile, original);
        Refused(() => CompactGatewayConfigurationCodec.Read(publicFile, roots));
        System.IO.File.WriteAllText(file, "{");
        Refused(() => CompactGatewayConfigurationCodec.Read(file, roots));
        PrivateFile.WriteAllBytes(file, new byte[CompactGatewayConfigurationCodec.MaximumBytes + 1], replace: true);
        Refused(() => CompactGatewayConfigurationCodec.Read(file, roots));
        Write(file, original);
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Refused(() => CompactGatewayConfigurationCodec.Read(file, roots));
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string link = Path.Combine(Path.GetDirectoryName(file)!, "linked.json");
            System.IO.File.CreateSymbolicLink(link, file);
            Refused(() => CompactGatewayConfigurationCodec.Read(link, roots));
        }
    }

    private static void Refused(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Expected native UI configuration refusal.");
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCompatibilityEnvironment.Require(condition, name);
    }
}
