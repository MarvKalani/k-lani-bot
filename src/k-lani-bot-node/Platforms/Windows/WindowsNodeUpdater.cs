using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Fleet;
using KLaniBot.Guard;
using KLaniBot.Startup;
using KLaniBot.Transport;
using KLaniBot.Updates;

namespace KLaniBot.Platforms.Windows;

// A Windows node offered an update by its master downloads and stages it here; a separate step replaces the installed executable and puts
// the old one back if the new one does not come up.
public static class WindowsNodeUpdater
{
    public static string InstalledExecutable()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", ProductIdentity.NodeName, ProductIdentity.NodeName + ".exe");
    }

    // Throws InvalidOperationException when this is not the installed node.
    public static async Task<string?> StageAsync(JsonObject paired, NodePolicy policy, string configPath, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows() || !IsInstalledExecutable(Environment.ProcessPath!))
        {
            throw new InvalidOperationException("Automatic replacement requires the installed Windows node.");
        }
        string directory = Path.Combine(policy.StateRoot, "updates");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        string? blockedVersion = RolledBackVersion(policy.StateRoot);
        NodeUpdateManifest? manifest = await NodeUpdateDownload.DownloadAsync(
            paired,
            policy.NodeId,
            directory,
            blockedVersion,
            ProductIdentity.DisplayVersion,
            deadline.Token);
        if (manifest == null)
        {
            return null;
        }
        var plan = new JsonObject
        {
            ["version"] = 1,
            ["parent_pid"] = Environment.ProcessId,
            ["config"] = Path.GetFullPath(configPath),
            ["manifest"] = JsonSerializer.SerializeToNode(manifest)
        };
        string path = Path.Combine(directory, "update-plan.json");
        PairingProtocol.WritePrivate(path, plan.ToJsonString(), true);
        return path;
    }

    // A version that failed its health check here is not installed again until a corrected one is published.
    private static string? RolledBackVersion(string stateRoot)
    {
        string statusFile = Path.Combine(stateRoot, "last-update.json");
        CartridgeManifest.RejectLinks(statusFile);
        if (!File.Exists(statusFile))
        {
            return null;
        }

        JsonObject status = JsonNode.Parse(File.ReadAllText(statusFile))!.AsObject();
        return status["status"]?.ToString() == "rolled_back" ? status["version"]?.ToString() : null;
    }

    // Throws IOException when the updater does not start.
    public static void StartApply(string planPath, string stateDirectory)
    {
        JsonObject plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        NodeUpdateManifest manifest = plan["manifest"]!.Deserialize<NodeUpdateManifest>()!;
        string executable = Path.Combine(Path.GetDirectoryName(planPath)!, "k-lani-bot-node-" + manifest.Version + ".exe");
        manifest.VerifyFile(executable);
        // Keep the updater and its extracted runtime outside the old state root.
        // A rename update must be able to move that root after the worker exits.
        string helperDirectory = Path.Combine(Path.GetTempPath(), "k-lani-bot-update-" + Guid.CreateVersion7());
        PairingClient.CreatePrivateDirectory(helperDirectory);
        string helper = Path.Combine(helperDirectory, "k-lani-bot-update.exe");
        File.Copy(executable, helper);
        manifest.VerifyFile(helper);
        var start = new ProcessStartInfo { FileName = helper, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(planPath);
        WorkerEnvironment.Configure(start, helperDirectory, WorkerPlatform.Windows);
        using var process = Process.Start(start) ?? throw new IOException("Verified updater could not start.");
    }

    // A plan this helper refuses, because it is invalid or not this node's, restarts the unchanged installed node instead.
    // Throws PlatformNotSupportedException outside Windows.
    public static async Task<int> ApplyAsync(string planPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This update installer targets Windows.");
        }
        UpdateJob job;
        try
        {
            job = ReadVerifiedJob(planPath);
            await WaitForParentExitAsync(job.ParentId, job.Installed);
        }
        catch (Exception exception)
        {
            // The node that staged the plan has already exited. Whatever made this helper refuse the plan, it must not leave the
            // machine without a node, so the unchanged installed node is started again.
            return await RestartAfterRefusedPlanAsync(planPath, exception);
        }

        string backup = job.Installed + ".previous";
        CartridgeManifest.RejectLinks(backup);
        File.Copy(job.Installed, backup, true);
        try
        {
            PairingClient.CreatePrivateDirectory(Path.GetDirectoryName(job.Installed)!);
            await CopyWithDeadlineAsync(job.Source, job.Installed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return await RollBackAsync(backup, job.Installed, job.ConfigPath, job.Policy.StateRoot, job.Manifest.Version);
        }
        return await StartReplacementAsync(job, backup);
    }

    private static async Task<int> RestartAfterRefusedPlanAsync(string planPath, Exception refusal)
    {
        UpdatePlanRecovery recovery = UpdatePlanRecovery.Read(planPath);
        string reason = refusal.GetType().Name + ": " + refusal.Message;
        Console.Error.WriteLine("Update plan refused; restarting the installed node. " + reason);
        bool previousStillRuns = await PreviousNodeStillRunsAsync(recovery.ParentId);
        PairingProtocol.WritePrivate(
            Path.Combine(recovery.StateRoot, "last-update.json"),
            recovery.RolledBackStatus(reason).ToJsonString(),
            true);

        if (!previousStillRuns && recovery.ConfigPath != null)
        {
            using Process restored = StartNode(InstalledExecutable(), recovery.ConfigPath, recovery.StateRoot);
        }
        return 1;
    }

    // Starting a second node next to one that is still shutting down would leave two answering the master.
    private static async Task<bool> PreviousNodeStillRunsAsync(int? parentId)
    {
        if (parentId == null)
        {
            return false;
        }
        try
        {
            using Process parent = Process.GetProcessById(parentId.Value);
            using var deadline = new CancellationTokenSource(ParentExitTimeout);
            await parent.WaitForExitAsync(deadline.Token);
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    // A checked update plan: this node's configuration and policy, the signed manifest, the installed executable and the
    // updater itself as the source of the new bytes.
    private sealed record UpdateJob(
        string ConfigPath,
        NodePolicy Policy,
        NodeUpdateManifest Manifest,
        string Installed,
        string Source,
        int ParentId);

    // Throws InvalidDataException for an invalid plan and UnauthorizedAccessException when plan, configuration or version are
    // not this node's.
    private static UpdateJob ReadVerifiedJob(string planPath)
    {
        CartridgeManifest.RejectLinks(planPath);
        JsonObject plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        if (plan.Count != 4 || plan["version"]?.GetValue<int>() != 1)
        {
            throw new InvalidDataException("Invalid update plan.");
        }
        string configPath = Path.GetFullPath(plan["config"]!.ToString());
        CartridgeManifest.RejectLinks(configPath);
        NodeConfiguration configuration = NodeConfiguration.Load(configPath);
        NodePolicy policy = NodePolicy.Load(configuration.PolicyPath);
        if (Path.GetFullPath(planPath) != Path.Combine(policy.StateRoot, "updates", "update-plan.json"))
        {
            throw new UnauthorizedAccessException("Update plan must belong to this node's private state.");
        }
        NodeUpdateManifest manifest = plan["manifest"]!.Deserialize<NodeUpdateManifest>()!;
        string installed = InstalledExecutable();
        string worker = Path.GetFullPath(configuration.WorkerCommand[0]);
        if (!IsInstalledExecutable(worker))
        {
            throw new UnauthorizedAccessException("The update configuration is not an owned installed node.");
        }
        CartridgeManifest.RejectLinks(installed);
        string source = Environment.ProcessPath!;
        // The staging node verified the signature under its own version's identifiers before it wrote this plan into its private
        // state. Checking it again here would strand every node whose version signed differently; the helper instead requires a
        // version newer than the installed executable and the exact bytes of the manifest.
        manifest.ValidateFields(WindowsExecutableMetadata.ReadProductVersion(installed), DateTimeOffset.UtcNow);
        if (manifest.Version != ProductIdentity.DisplayVersion)
        {
            throw new UnauthorizedAccessException("The updater version differs from the signed release.");
        }
        manifest.VerifyFile(source);
        return new UpdateJob(configPath, policy, manifest, installed, source, plan["parent_pid"]!.GetValue<int>());
    }

    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(30);

    // Throws UnauthorizedAccessException when the waiting parent is not the installed node.
    private static async Task WaitForParentExitAsync(int parentId, string installed)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentId);
            if (!Path.GetFullPath(parent.MainModule!.FileName).Equals(installed, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("The update parent is not the installed node.");
            }
            using var deadline = new CancellationTokenSource(ParentExitTimeout);
            await parent.WaitForExitAsync(deadline.Token);
        }
        catch (ArgumentException)
        {
            // The verified supervisor may already have exited before this helper starts.
        }
    }

    // The previous executable stays until the new one reports a visible worker and a master connection; otherwise it is
    // put back.
    private static async Task<int> StartReplacementAsync(UpdateJob job, string backup)
    {
        string readiness = Path.Combine(job.Policy.StateRoot, "node-ready.json");
        CartridgeManifest.RejectLinks(readiness);
        File.Delete(readiness);
        using Process updated = StartNode(job.Installed, job.ConfigPath, job.Policy.StateRoot);
        bool healthy = await WaitForHealthAsync(updated, readiness, job.Manifest.Version);
        if (!healthy)
        {
            if (!updated.HasExited)
            {
                updated.Kill(true);
                await updated.WaitForExitAsync();
            }
            return await RollBackAsync(backup, job.Installed, job.ConfigPath, job.Policy.StateRoot, job.Manifest.Version);
        }
        // Do not remove the fallback until the replacement has established a visible worker and authenticated master connection.
        File.Delete(backup);
        PairingProtocol.WritePrivate(Path.Combine(job.Policy.StateRoot, "last-update.json"),
            new JsonObject { ["version"] = job.Manifest.Version, ["status"] = "completed" }.ToJsonString(), true);
        await NodeUpdateReporting.SendAsync(job.Policy);
        return 0;
    }

    // The replacement did not start or did not become healthy, so the previous executable is put back and
    // started again. The master can read the outcome from the state directory.
    private static async Task<int> RollBackAsync(string backup, string installed, string configPath, string stateRoot, string version)
    {
        await CopyWithDeadlineAsync(backup, installed);
        using Process restored = StartNode(installed, configPath, stateRoot);
        PairingProtocol.WritePrivate(Path.Combine(stateRoot, "last-update.json"),
            new JsonObject { ["version"] = version, ["status"] = "rolled_back" }.ToJsonString(), true);
        await NodeUpdateReporting.SendAsync(NodePolicy.Load(NodeConfiguration.Load(configPath).PolicyPath));
        return 1;
    }

    private static bool IsInstalledExecutable(string path)
    {
        return path.Equals(InstalledExecutable(), StringComparison.OrdinalIgnoreCase);
    }

    public static async Task CleanupStagingAsync(string stateDirectory, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), cancellation);
            string directory = Path.Combine(stateDirectory, "updates");
            if (!Directory.Exists(directory))
            {
                return;
            }
            CartridgeManifest.RejectLinks(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "k-lani-bot-node-*.exe").Take(1000))
            {
                string version = Path.GetFileNameWithoutExtension(file)["k-lani-bot-node-".Length..];
                if (!NodeUpdateManifest.IsProductVersion(version)
                    || System.Version.Parse(version) > System.Version.Parse(ProductIdentity.DisplayVersion))
                {
                    continue;
                }
                try
                {
                    // Never remove a future download; a still-running helper keeps its current image locked on Windows.
                    CartridgeManifest.RejectLinks(file);
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Failed housekeeping cannot interrupt the visible worker or an OS action.
        }
    }

    private static async Task CopyWithDeadlineAsync(string source, string destination)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                string incoming = destination + ".incoming";
                CartridgeManifest.RejectLinks(incoming);
                File.Copy(source, incoming, true);
                File.Move(incoming, destination, true);
                return;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Windows can release an executable mapping shortly after its process exits.
                await Task.Delay(250);
            }
        }
    }

    // Throws IOException when the node does not start.
    private static Process StartNode(string executable, string configPath, string stateDirectory)
    {
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--connect");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);
        WorkerEnvironment.Configure(start, stateDirectory, WorkerPlatform.Windows);
        return Process.Start(start) ?? throw new IOException("Replacement node could not start.");
    }

    private static async Task<bool> WaitForHealthAsync(Process process, string path, string version)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited)
        {
            if (File.Exists(path))
            {
                try
                {
                    JsonObject ready = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                    if (ready["pid"]?.GetValue<int>() == process.Id && ready["version"]?.ToString() == version &&
                        ready["master_connected"]?.GetValue<bool>() == true && ready["worker_ready"]?.GetValue<bool>() == true)
                    {
                        return true;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // The supervisor atomically replaces this marker; a transient observation is retried.
                }
            }
            await Task.Delay(250);
        }
        return false;
    }
}
