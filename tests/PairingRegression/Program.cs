using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Cartridges;
using KLaniBot.Fleet;
using KLaniBot.Inventory;
using KLaniBot.Platforms.Windows;
using KLaniBot.Transport;
using KLaniBot.Updates;
using KLaniBot.Visibility;
using KLaniBot.Startup;

// Pairing regression: pairs real node processes with a real master in a private display, and checks pairing, enrollment, relay, updates and
// revocation.
internal static partial class Program
{
    private static int _passed;

    public static async Task Main(string[] arguments)
    {
        // These runs start a node whose worker opens real windows. Refuse unless the display is private.
        PrivateGuiSession.Require();
        TestProtocol();
        Denied(() => ExecutionRoleSelector.FromArguments(["--mcp", "--enroll", "file"]), "Controller cannot become a desktop installer.");
        Denied(
            () => ExecutionRoleSelector.FromArguments(["--mcp", "--apply-update", "file"]),
            "Controller cannot become an update installer.");
        TestUpdates();
        TestFleetInvitations();
        await TestLostReplyAsync();
        string binary = Path.GetFullPath(arguments[0]);
        string temporary = Path.Combine(Path.GetTempPath(), "k-lani-bot-pairing-" + Guid.CreateVersion7());
        Directory.CreateDirectory(temporary);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            await TestConnectionAsync(binary, temporary, 4);
            await TestConnectionAsync(binary, temporary, 0);
            await TestWrongCodeAsync(binary, temporary);
            await TestManagedEnrollmentAsync(binary, temporary);
            if (arguments.Length > 1)
            {
                await TestPublishedDownloadAsync(binary, temporary, Path.GetFullPath(arguments[1]));
            }
            Console.WriteLine($"{_passed} pairing regression checks passed.");
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition)
        {
            throw new Exception("FAILED: " + reason);
        }
        _passed++;
    }

    private static void Denied(Action operation, string reason)
    {
        try
        {
            operation();
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or InvalidDataException)
        {
            _passed++;
            return;
        }
        throw new Exception("FAILED: " + reason);
    }

    private static void TestProtocol()
    {
        TestCodeOptions();
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo());
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PairingRequest Create()
        {
            return new PairingRequest("fixture", PairingProtocol.Commitment(nonce, publicKey), "test-root", new PairingOptions(), now);
        }
        var pending = Create();
        Denied(() => pending.Approve(now), "Approval before commitment reveal.");
        pending.Reveal(nonce, publicKey, now);
        string signature = Convert.ToBase64String(identity.SignData(Encoding.UTF8.GetBytes(pending.Transcript), HashAlgorithmName.SHA256));
        Denied(() => pending.Complete(signature, now), "Completion without administrator approval.");
        pending.Approve(now);
        pending.Complete(signature, now);
        Check(pending.State == PairingState.Consumed, "Successful pairing consumes the request.");
        Denied(() => pending.Complete(signature, now), "Successful proof cannot be replayed.");
        var expired = Create();
        expired.Reveal(nonce, publicKey, now);
        Denied(() => expired.Approve(now.AddMinutes(5)), "Expired requests cannot be approved.");
        var invalidProof = Create();
        invalidProof.Reveal(nonce, publicKey, now);
        invalidProof.Approve(now);
        Denied(() => invalidProof.Complete(Convert.ToBase64String(new byte[64]), now), "Invalid device signature.");
        Check(invalidProof.State == PairingState.Consumed, "Failed proof consumes the sole attempt.");
        var changedNonce = Create();
        string different = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Denied(() => changedNonce.Reveal(different, publicKey, now), "Nonce grinding after commitment.");
        Check(changedNonce.State == PairingState.Rejected, "Changed commitment permanently rejected.");
    }

    private static void TestCodeOptions()
    {
        string transcript = PairingProtocol.Hash("test", "transcript");
        for (int digits = 0; digits <= 6; digits++)
        {
            string code = PairingProtocol.Code(transcript, digits);
            Check(code.Length == digits && code.All(char.IsAsciiDigit), "Every configurable code length works.");
        }
        Denied(() => new PairingOptions { CodeDigits = -1 }.Validate(), "Negative code length.");
        Denied(() => new PairingOptions { CodeDigits = 7 }.Validate(), "Oversized code length.");
        Denied(() => new PairingOptions { LifetimeSeconds = 301 }.Validate(), "Excessive pairing lifetime.");
        Check(PairingProtocol.Hash("ab", "c") != PairingProtocol.Hash("a", "bc"), "Unambiguous transcript fields.");
        Denied(
            () => new PairingRequest("../escape", new string('a', 64), "root", new PairingOptions(), DateTimeOffset.UtcNow),
            "Node identifier path traversal.");
    }

    private static void TestUpdates()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=update-test", key, HashAlgorithmName.SHA256);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var root = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(31));
        string encodedRoot = Convert.ToBase64String(root.RawData);
        string directory = Path.Combine(Path.GetTempPath(), "k-lani-bot-update-test-" + Guid.CreateVersion7());
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(4096);
            string file = Path.Combine(directory, "fixture.bin");
            File.WriteAllBytes(file, bytes);
            var manifest = new NodeUpdateManifest("0.2610.0103", "windows-x64", bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), now, now.AddDays(7), "");
            manifest = manifest with
            {
                Signature = Convert.ToBase64String(key.SignData(manifest.SignedBytes(), HashAlgorithmName.SHA256))
            };
            manifest.Validate(encodedRoot, "0.2610.0102", now);
            Check(true, "Signed newer release accepted.");
            manifest.VerifyFile(file);
            Check(true, "Actual file size and hash match the signed release.");
            TestManifestRefusals(manifest, encodedRoot, now);
            bytes[0] ^= 1;
            File.WriteAllBytes(file, bytes);
            Denied(() => manifest.VerifyFile(file), "Modified executable bytes rejected.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Check(!NodeUpdateManifest.IsProductVersion("../escape"), "Update version path traversal rejected.");
        Check(!NodeUpdateManifest.IsProductVersion("0.2613.0101"), "Invalid release month rejected.");
        Check(!NodeUpdateManifest.IsProductVersion("0.2610.0100"), "Zero release counter rejected.");
        Check(NodeUpdateManifest.IsProductVersion("0.2610.0102"), "Leading-zero release date accepted.");
    }

    private static void TestManifestRefusals(NodeUpdateManifest manifest, string encodedRoot, DateTimeOffset now)
    {
        Denied(
            () => (manifest with { Version = "0.2610.0104" }).Validate(encodedRoot, "0.2610.0102", now),
            "Signed version tampering.");
        Denied(
            () => (manifest with { Sha256 = new string('0', 64) }).Validate(encodedRoot, "0.2610.0102", now),
            "Signed digest tampering.");
        Denied(() => manifest.Validate(encodedRoot, "0.2610.0103", now), "Same version is not automatically reinstalled.");
        Denied(() => manifest.Validate(encodedRoot, "0.2610.0104", now), "Automatic downgrade rejected.");
        Denied(() => manifest.Validate(encodedRoot, "0.2610.0102", now.AddDays(8)), "Expired update rejected.");
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherRoot = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=other",
            otherKey,
            HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddMinutes(-1), now.AddDays(31));
        Denied(
            () => manifest.Validate(Convert.ToBase64String(otherRoot.RawData), "0.2610.0102", now),
            "Different master cannot sign this node's update.");
    }

    private static void TestFleetInvitations()
    {
        var invitations = new FleetInvitationStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo());
        JsonObject invite = invitations.Invite("managed", "https://localhost/", "root", ConsentMode.Unattended, now);
        string transcript = PairingProtocol.Hash(ProtocolIdentity.FleetProofDomain, "managed", invite["challenge"]!.ToString(), publicKey);
        string signature = Convert.ToBase64String(identity.SignData(Encoding.UTF8.GetBytes(transcript), HashAlgorithmName.SHA256));
        JsonObject settings = invitations.Consume(invite["token"]!.ToString(), "managed", publicKey, signature, now);
        Check(settings["consent_mode"]!.ToString() == "unattended", "Administrator deliberately selects unattended fleet operation.");
        Denied(() => invitations.Consume(invite["token"]!.ToString(), "managed", publicKey, signature, now), "Fleet invite is single-use.");
        JsonObject expired = invitations.Invite("managed", "https://localhost/", "root", ConsentMode.Ask, now);
        Denied(
            () => invitations.Consume(expired["token"]!.ToString(), "managed", publicKey, signature, now.AddHours(2)),
            "Expired fleet invite rejected.");
        JsonObject wrongNode = invitations.Invite("managed", "https://localhost/", "root", ConsentMode.Ask, now);
        Denied(
            () => invitations.Consume(wrongNode["token"]!.ToString(), "different", publicKey, signature, now),
            "Invite cannot authorize a different node.");
        JsonObject invalidProof = invitations.Invite("managed", "https://localhost/", "root", ConsentMode.Ask, now);
        Denied(
            () => invitations.Consume(invalidProof["token"]!.ToString(), "managed", publicKey, Convert.ToBase64String(new byte[64]), now),
            "Fleet enrollment requires device-key proof.");
        Denied(() => invitations.Consume("not-an-invitation", "managed", publicKey, signature, now), "Unknown fleet token rejected.");
    }

    private static async Task TestManagedEnrollmentAsync(string binary, string temporary)
    {
        string masterState = Path.Combine(temporary, "managed-master");
        string nodeState = Path.Combine(temporary, "managed-node");
        using var master = new MasterProcess(binary, masterState, FreePort());
        await master.WaitForAsync("k-lani-bot Master ");
        string invitationPath = Path.Combine(temporary, "deployment-invite.json");
        string config = await EnrollAsync(
            binary, masterState, invitationPath, nodeState, Path.Combine(temporary, "managed-workspace"),
            ["--node", "managed", "--scopes", "observe,files.read,files.write", "--consent-mode", "unattended"],
            "invite managed unattended");
        Check(
            config == Path.Combine(nodeState, "node.json") && !File.Exists(invitationPath),
            "Silent authenticated enrollment consumes the private invitation without a code prompt.");
        JsonObject registry = JsonNode.Parse(File.ReadAllText(Path.Combine(masterState, "nodes.json")))!.AsObject();
        Check(registry["nodes"]?["managed"] != null, "Managed node registered at master.");

        await WithConnectedNodeAsync(binary, config, async () =>
        {
            await master.WaitForAsync("Connected: managed");
            using var client = new NodeTransportClient("managed", Path.Combine(masterState, "nodes.json"), forceH2: true);
            JsonObject result = await client.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), CancellationToken.None);
            Check(result["success"]?.GetValue<bool>() == true, "Managed node reaches real visible worker without manual pairing.");
            await TestInventoryPermissionAsync(client, masterState);
            await TestBackupRequirementAsync(client, master, masterState);
            await TestServiceScanAsync(masterState, temporary);
            await TestControllerAccessAsync(binary, masterState);
            await MasterAdministration.CallAsync(masterState, "service-inventory managed off", CancellationToken.None);
            JsonObject revoked = await client.CallAsync(Tool.CollectInventory, new JsonObject(), CancellationToken.None);
            Check(
                revoked["success"]?.GetValue<bool>() != true,
                "Inventory permission can be centrally revoked while other scopes remain intact.");
        });
        await TestFailedWorkerManagementAsync(binary, config, masterState, temporary);
    }

    private static async Task TestFailedWorkerManagementAsync(string binary, string config, string masterState, string temporary)
    {
        JsonObject configuration = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        configuration["worker"] = new JsonArray(binary, "--export-schemas", Path.Combine(temporary, "failed-worker-schemas"));
        string brokenConfig = Path.Combine(temporary, "failed-worker.json");
        File.WriteAllText(brokenConfig, configuration.ToJsonString());
        await WithConnectedNodeAsync(binary, brokenConfig, async () =>
        {
            using var client = new NodeTransportClient("managed", Path.Combine(masterState, "nodes.json"), forceH2: true);
            JsonObject status = new();
            for (int attempt = 0; attempt < 40; attempt++)
            {
                status = await client.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), CancellationToken.None);
                if (status["management"]?["worker_available"]?.GetValue<bool>() == false)
                {
                    break;
                }
                await Task.Delay(100);
            }
            Check(status["success"]?.GetValue<bool>() == true
                && status["management"]?["worker_available"]?.GetValue<bool>() == false,
                "The existing paired channel remains authenticated and diagnostic-capable when the GUI worker never starts.");
            JsonObject refused = await client.CallAsync(Tool.GetUiTree, new JsonObject(), CancellationToken.None);
            Check(refused["outcome"]?.ToString() == "not_dispatched",
                "A failed GUI worker cannot receive desktop input while management remains available.");
            JsonObject second = await client.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), CancellationToken.None);
            Check(second["success"]?.GetValue<bool>() == true,
                "A refused desktop request does not close the node's management connection.");
            await TestUpdateStatusUploadAsync(masterState, config, client);
        });
    }

    private static async Task TestUpdateStatusUploadAsync(string masterState, string config, NodeTransportClient controller)
    {
        NodeConfiguration configuration = NodeConfiguration.Load(config);
        KLaniBot.Guard.NodePolicy policy = KLaniBot.Guard.NodePolicy.Load(configuration.PolicyPath);
        string pairedPath = Path.Combine(policy.StateRoot, "paired-master.json");
        JsonObject paired = JsonNode.Parse(File.ReadAllText(pairedPath))!.AsObject();
        using HttpClient client = NodeUpdateDownload.PinnedClient(paired, policy.NodeId);
        var report = new JsonObject
        {
            ["supervisor_version"] = "0.2610.0509",
            ["last_update"] = new JsonObject { ["version"] = "0.2610.0509", ["status"] = "rolled_back" },
            ["startup_failure"] = new JsonObject { ["diagnostic"] = "Synthetic GUI failure retained after rollback." }
        };
        using HttpResponseMessage response = await client.PostAsync(ProtocolIdentity.UpdateStatusRoute,
            new StringContent(report.ToJsonString(), Encoding.UTF8, "application/json"));
        Check(response.IsSuccessStatusCode, "An update helper uploads diagnostics using the existing node credential and CA.");
        JsonObject queried = await controller.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), CancellationToken.None);
        Check(queried["update_report"]?["startup_failure"]?["diagnostic"]?.ToString()
            == "Synthetic GUI failure retained after rollback.",
            "The master returns uploaded failure evidence over the existing MCP channel.");
    }

    // The invitation is created on the master's console, the node is initialized locally and then enrolls with it.
    private static async Task<string> EnrollAsync(
        string binary,
        string masterState,
        string invitationPath,
        string nodeState,
        string workspace,
        string[] nodeArguments,
        string inviteCommand)
    {
        JsonObject response = await MasterAdministration.CallAsync(masterState, inviteCommand, CancellationToken.None);
        Check(response["success"]?.GetValue<bool>() == true, "Local administrator can prepare a device-specific invitation.");
        PairingProtocol.WritePrivate(invitationPath, response["invitation"]!.ToJsonString());
        var initialized = await RunOnceAsync(binary,
            ["--init", "--backup-requirement", "off", "--workspace", workspace, "--state", nodeState, .. nodeArguments]);
        Check(initialized.Exit == 0, initialized.Error);
        return await FleetEnrollment.EnrollAsync(invitationPath, nodeState, "not-used-for-existing-policy", CancellationToken.None);
    }

    // Starts the paired node, runs the checks while it is connected, and always stops it again.
    private static async Task WithConnectedNodeAsync(string binary, string config, Func<Task> checks)
    {
        using var node = Start(binary, ["--connect", "--config", config]);
        Task<string> errors = node.StandardError.ReadToEndAsync();
        Task<string> output = node.StandardOutput.ReadToEndAsync();
        try
        {
            await checks();
        }
        finally
        {
            if (!node.HasExited)
            {
                node.Kill(true);
            }
            await node.WaitForExitAsync();
            await errors;
            await output;
        }
    }

    private static async Task TestInventoryPermissionAsync(NodeTransportClient client, string masterState)
    {
        JsonObject inventoryDenied = await client.CallAsync(Tool.CollectInventory, new JsonObject(), CancellationToken.None);
        Check(
            inventoryDenied["success"]?.GetValue<bool>() != true,
            "New inventory capability is not silently granted to an existing node.");
        JsonObject permission = await MasterAdministration.CallAsync(
            masterState,
            "service-inventory managed on",
            CancellationToken.None);
        Check(
            permission["success"]?.GetValue<bool>() == true,
            "Local master administrator can request the specific inventory permission.");
        JsonObject inventoryResult = await client.CallAsync(
            "collect_inventory",
            new JsonObject { ["limit"] = 1 },
            CancellationToken.None);
        Check(
            inventoryResult["success"]?.GetValue<bool>() == true,
            "Explicit inventory grant reaches supervisor and worker without a restart.");
    }

    private static async Task TestBackupRequirementAsync(NodeTransportClient client, MasterProcess master, string masterState)
    {
        string order = "backup-requirement managed required";
        JsonObject ordered = await MasterAdministration.CallAsync(masterState, order, CancellationToken.None);
        bool sent = ordered["sent"]?.AsArray().Any(node => node?.ToString() == "managed") == true;
        Check(sent, "The master sends the order to a connected node.");
        await master.WaitForAsync("Backup requirement applied: managed");
        JsonObject refused = await client.CallAsync(Tool.FsWriteFile, WriteRequest("required.txt"), CancellationToken.None);
        Check(refused["code"]?.ToString() == "backup_required", "A remotely required backup stops changes on the node.");

        await MasterAdministration.CallAsync(masterState, "backup-requirement all off", CancellationToken.None);
        await master.WaitForAsync("Backup requirement applied: managed");
        JsonObject allowed = await client.CallAsync(Tool.FsWriteFile, WriteRequest("off.txt"), CancellationToken.None);
        Check(allowed["success"]?.GetValue<bool>() == true, "Switching the requirement off for all nodes applies without a restart.");
        JsonObject status = await MasterAdministration.CallAsync(masterState, "backup-requirement-status", CancellationToken.None);
        Check(status["nodes"]?["managed"]?["applied"]?.GetValue<bool>() == true, "The master reports the order as applied.");
    }

    private static JsonObject WriteRequest(string path)
    {
        return new JsonObject { ["path"] = path, ["content"] = "remote switch", ["request_id"] = Guid.CreateVersion7().ToString() };
    }

    private static async Task TestServiceScanAsync(string masterState, string temporary)
    {
        string targetPath = Path.Combine(temporary, "service-target-fixture.json");
        string cataloguePath = Path.Combine(temporary, "service-catalogue-fixture.json");
        var target = new CompatibilityTarget(
            "linux-fixture",
            "Synthetic inventory coverage check",
            "linux",
            "fixture",
            "X64",
            ["installed-applications"]);
        PairingProtocol.WritePrivate(targetPath, System.Text.Json.JsonSerializer.Serialize(target, SystemInventory.JsonOptions));
        PairingProtocol.WritePrivate(cataloguePath, "[]");
        JsonObject configured = await MasterAdministration.CallAsync(
            masterState,
            "service-configure " + targetPath + " " + cataloguePath,
            CancellationToken.None);
        Check(configured["success"]?.GetValue<bool>() == true, "Master accepts a scoped assessment profile and catalogue.");

        JsonObject scan = await MasterAdministration.CallAsync(masterState, "service-scan managed", CancellationToken.None);
        JsonObject status = await WaitForScanAsync(masterState, scan["job_id"]!.ToString());
        Check(status["success"]?.GetValue<bool>() == true && status["state"]?.ToString() == "Blocked",
            "Real node scan reports missing Linux application coverage instead of falsely approving compatibility.");
        Check(File.Exists(Path.Combine(masterState, "service-workflows", status["assessment_id"]!.ToString() + ".report.json")),
            "Version-bound assessment persisted privately at master.");
    }

    private static async Task<JsonObject> WaitForScanAsync(string masterState, string job)
    {
        JsonObject status = new();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            status = await MasterAdministration.CallAsync(masterState, "service-status " + job, CancellationToken.None);
            if (status["state"]?.ToString() != "collecting")
            {
                break;
            }
            await Task.Delay(50);
        }
        return status;
    }

    private static async Task TestPublishedDownloadAsync(string binary, string temporary, string executable)
    {
        string masterState = Path.Combine(temporary, "update-master");
        string nodeState = Path.Combine(temporary, "update-node");
        using var master = new MasterProcess(binary, masterState, FreePort());
        await master.WaitForAsync("k-lani-bot Master ");
        await EnrollAsync(
            binary, masterState, Path.Combine(temporary, "download-invite.json"), nodeState, Path.Combine(temporary, "update-workspace"),
            ["--node", "download-test", "--scopes", "observe", "--consent-mode", "ask"], "invite download-test ask");
        string version = WindowsExecutableMetadata.ReadProductVersion(executable);
        Check(NodeUpdateManifest.IsProductVersion(version), "Native Windows PE product identity and version read correctly.");
        JsonObject published = await MasterAdministration.CallAsync(
            masterState,
            "publish " + version + " " + executable,
            CancellationToken.None);
        Check(published["success"]?.GetValue<bool>() == true, "Only the local administrator publishes an immutable signed executable.");

        JsonObject paired = JsonNode.Parse(File.ReadAllText(Path.Combine(nodeState, "paired-master.json")))!.AsObject();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string downloads = Path.Combine(temporary, "downloads");
        NodeUpdateManifest manifest = (await NodeUpdateDownload.DownloadAsync(
            paired,
            "download-test",
            downloads,
            null,
            "0.0000.0000",
            deadline.Token))!;
        Check(manifest.Version == version, "Authenticated H3/H2 download receives the published manifest.");
        manifest.VerifyFile(Path.Combine(downloads, "k-lani-bot-node-" + version + ".exe"));
        Check(true, "Downloaded executable independently matches the signed hash and size.");
        paired["credential"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await RequireUnauthorizedDownloadAsync(paired, downloads, deadline.Token);
    }

    private static async Task RequireUnauthorizedDownloadAsync(JsonObject paired, string downloads, CancellationToken cancellation)
    {
        try
        {
            await NodeUpdateDownload.DownloadAsync(paired, "download-test", downloads, null, "0.0000.0000", cancellation);
            throw new Exception("FAILED: Update download accepted an unauthorized device.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            _passed++;
        }
    }

    private static async Task TestLostReplyAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
            await connecting;
            using var serverSocket = System.Net.WebSockets.WebSocket.CreateFromStream(
                server.GetStream(),
                true,
                null,
                TimeSpan.FromSeconds(20));
            using var clientSocket = System.Net.WebSockets.WebSocket.CreateFromStream(
                client.GetStream(),
                false,
                null,
                TimeSpan.FromSeconds(20));
            await TestRelayLosesReplyAsync(serverSocket, clientSocket, client, deadline.Token);
        }
        finally
        {
            listener.Stop();
        }
    }

    // The node side disappears after the request was sent, so the controller must hear "unknown" and the relay must close.
    private static async Task TestRelayLosesReplyAsync(
        System.Net.WebSockets.WebSocket serverSocket,
        System.Net.WebSockets.WebSocket clientSocket,
        TcpClient client,
        CancellationToken cancellation)
    {
        using var relay = new ReverseRelay(serverSocket);
        Task reading = relay.ReadAsync(cancellation);
        Task<JsonObject> result = relay.CallAsync(new JsonObject { ["kind"] = "action" }, cancellation);
        JsonObject request = (await PairingProtocol.ReceiveAsync(clientSocket, cancellation))!;
        Check(Guid.Parse(request["id"]!.ToString()).Version == 7, "Relay correlation uses UUIDv7.");
        clientSocket.Abort();
        client.Dispose();
        Check((await result)["outcome"]?.ToString() == "unknown", "Disconnect after dispatch reports unknown; never assumes failure.");
        var replayReply = await relay.CallAsync(new JsonObject { ["kind"] = "action" }, cancellation);
        Check(replayReply["outcome"]?.ToString() == "not_dispatched", "Closed relay cannot replay a mutation.");
        try
        {
            await reading;
        }
        catch (System.Net.WebSockets.WebSocketException)
        {
        }
    }

    private static async Task TestConnectionAsync(string binary, string temporary, int digits)
    {
        string masterState = Path.Combine(temporary, "master-" + digits);
        string nodeState = Path.Combine(temporary, "node-" + digits);
        string workspace = Path.Combine(temporary, "workspace-" + digits);
        Directory.CreateDirectory(nodeState);
        int port = FreePort();
        var initialized = await RunOnceAsync(binary, ["--init", "--backup-requirement", "off", "--workspace", workspace,
            "--state", nodeState, "--node", "fixture", "--scopes", "observe,files.read,files.write", "--consent-mode", "unattended"]);
        Check(initialized.Exit == 0, initialized.Error);
        using var master = new MasterProcess(binary, masterState, port);
        await master.WaitForAsync("k-lani-bot Master ");
        await PairWithApprovalAsync(master, masterState, nodeState, port, digits);

        await WithConnectedNodeAsync(binary, Path.Combine(nodeState, "node.json"), async () =>
        {
            await master.WaitForAsync("Connected: fixture");
            using var client = new NodeTransportClient("fixture", Path.Combine(masterState, "nodes.json"), forceH2: true);
            string operation = await TestRelayedActionsAsync(client, workspace);
            master.Stop();
            using var restarted = new MasterProcess(binary, masterState, port);
            await TestRestartAndRevocationAsync(restarted, client, masterState, operation);
        });
    }

    // The code may be shown or disabled; either way only the administrator's approval completes the pairing.
    private static async Task PairWithApprovalAsync(MasterProcess master, string masterState, string nodeState, int port, int digits)
    {
        await master.CommandAsync("digits " + digits);
        await master.WaitForAsync("Code digits for new pairings: " + digits);
        using var pairing = new PairingClient("https://127.0.0.1:" + port);
        await pairing.BeginAsync("fixture", CancellationToken.None);
        string invitation = await master.WaitForAsync("Pairing: fixture | ticket ");
        string ticket = invitation.Split(" | ")[1]["ticket ".Length..];
        JsonObject pending = await MasterAdministration.CallAsync(masterState, "pending", CancellationToken.None);
        string code = pending["pending"]![0]!["code"]!.ToString();
        Check(pairing.CodeDigits == digits, "Master controls node code length.");
        Task finishing = pairing.FinishAsync(code, nodeState, CancellationToken.None);
        await Task.Delay(200);
        Check(!finishing.IsCompleted, "Code disabled or enabled never bypasses administrator approval.");
        await master.CommandAsync("approve " + ticket);
        await finishing.WaitAsync(TimeSpan.FromSeconds(15));
        Check(File.Exists(Path.Combine(nodeState, "paired-master.json")), "Private master binding persisted.");
        Denied(
            () => pairing.FinishAsync(code, nodeState, CancellationToken.None).GetAwaiter().GetResult(),
            "Second local attempt rejected.");
    }

    // Returns the id of the write, whose receipt must survive a master restart.
    private static async Task<string> TestRelayedActionsAsync(NodeTransportClient client, string workspace)
    {
        JsonObject capability = await client.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), CancellationToken.None);
        Check(
            capability["success"]?.GetValue<bool>() == true,
            "Real visible worker reached through outbound relay and prefixed registry URL.");
        Check(capability["transport"]?.ToString() == "h2", "Controller H2 authentication preserved.");
        JsonObject denied = await client.CallAsync(Tool.RunTerminalCommand, new JsonObject
        {
            ["command"] = "echo forbidden",
            ["request_id"] = Guid.CreateVersion7().ToString()
        }, CancellationToken.None);
        Check(denied["success"]?.GetValue<bool>() == false
            && denied["outcome"]?.ToString() == "not_dispatched", "Local scope enforcement survives relay.");
        string operation = Guid.CreateVersion7().ToString();
        JsonObject write = await client.CallAsync(Tool.FsWriteFile, new JsonObject
        {
            ["path"] = "once.txt",
            ["content"] = "only once",
            ["request_id"] = operation
        }, CancellationToken.None);
        Check(write["success"]?.GetValue<bool>() == true, "Authorized workspace mutation through relay: " + write.ToJsonString());
        Check(File.ReadAllText(Path.Combine(workspace, "once.txt")) == "only once", "Independent mutation result.");
        return operation;
    }

    private static async Task TestRestartAndRevocationAsync(
        MasterProcess restarted,
        NodeTransportClient client,
        string masterState,
        string operation)
    {
        await restarted.WaitForAsync("k-lani-bot Master ");
        await restarted.WaitForAsync("Connected: fixture");
        JsonObject status = await client.CallAsync(
            "get_action_status",
            new JsonObject { ["request_id"] = operation },
            CancellationToken.None);
        Check(
            status["outcome"]?.ToString() == "completed",
            "Master restart reconnects using saved keys and recovers receipt without replay.");
        await restarted.CommandAsync("revoke fixture");
        await restarted.WaitForAsync("Revoked: fixture");
        JsonObject blocked = await client.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), CancellationToken.None);
        Check(blocked["success"]?.GetValue<bool>() != true, "Revocation rejects the previous controller and node credentials.");
        Check(
            JsonNode.Parse(File.ReadAllText(Path.Combine(masterState, "nodes.json")))!["nodes"]!.AsObject().Count == 0,
            "Revoked node removed from registry.");
    }

    private static async Task TestWrongCodeAsync(string binary, string temporary)
    {
        string state = Path.Combine(temporary, "wrong-code-master");
        int port = FreePort();
        using var master = new MasterProcess(binary, state, port);
        await master.WaitForAsync("k-lani-bot Master ");
        using var pairing = new PairingClient("https://127.0.0.1:" + port);
        await pairing.BeginAsync("wrong-code", CancellationToken.None);
        string invitation = await master.WaitForAsync("Pairing: wrong-code | ticket ");
        string ticket = invitation.Split(" | ")[1]["ticket ".Length..];
        JsonObject pending = await MasterAdministration.CallAsync(state, "pending", CancellationToken.None);
        string code = pending["pending"]![0]!["code"]!.ToString();
        string incorrect = code == "0000" ? "0001" : "0000";
        Denied(() => pairing.FinishAsync(incorrect, temporary, CancellationToken.None).GetAwaiter().GetResult(), "Wrong code rejected.");
        await master.CommandAsync("approve " + ticket);
        await master.WaitForAsync("Command rejected: ");
        using var another = new PairingClient("https://127.0.0.1:" + port);
        try
        {
            await another.BeginAsync("new-identity", CancellationToken.None);
            throw new Exception("FAILED: Reconnect bypassed the five-minute attempt limit.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _passed++;
        }
        Check(
            JsonNode.Parse(File.ReadAllText(Path.Combine(state, "nodes.json")))!["nodes"]!.AsObject().Count == 0,
            "Wrong code never enrolls a node.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // An environment entry with a null value is removed from the child's environment.
    private static Process Start(string binary, string[] arguments, IReadOnlyDictionary<string, string?>? environment = null)
    {
        bool dll = binary.EndsWith(".dll", StringComparison.Ordinal);
        var start = new ProcessStartInfo(dll ? "dotnet" : binary)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
        {
            if (value == null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
        if (dll)
        {
            start.ArgumentList.Add(binary);
        }
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start)!;
    }

    // Exit code and error output of a node command run to its end.
    private sealed record ProcessRun(int Exit, string Error);

    private static async Task<ProcessRun> RunOnceAsync(string binary, string[] arguments)
    {
        using Process process = Start(binary, arguments);
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        return new ProcessRun(process.ExitCode, await error);
    }

    // A master started as its own process; a case waits for lines it prints and types console commands into it.
    private sealed class MasterProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly Task _reader;
        private readonly Task<string> _errors;

        public MasterProcess(string binary, string state, int port)
        {
            _process = Start(binary, ["--pairing-master", "--state", state, "--bind", "127.0.0.1", "--port", port.ToString()]);
            _errors = _process.StandardError.ReadToEndAsync();
            _reader = Task.Run(async () =>
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                {
                    await _lines.Writer.WriteAsync(line);
                }
                _lines.Writer.TryComplete();
            });
        }

        public async Task<string> WaitForAsync(string prefix)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await foreach (string line in _lines.Reader.ReadAllAsync(deadline.Token))
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return line;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw new Exception("Master did not report " + prefix);
            }
            throw new Exception("Master exited: " + await _errors);
        }

        public async Task CommandAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
        }

        public void Stop()
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit();
            }
        }

        public void Dispose()
        {
            Stop();
            _reader.GetAwaiter().GetResult();
            _errors.GetAwaiter().GetResult();
            _process.Dispose();
        }
    }
}
