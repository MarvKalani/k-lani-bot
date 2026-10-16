using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Routed acceptance tests never substitute a caller flag or ordinary result reference for independently issued signed evidence.
internal static class CompactGatewayCompatibilityCases
{
    public static async Task RunAsync()
    {
        await AssessmentAsync();
        await AcceptedAsync();
        await RefusedAsync();
        await ForeignAndChangedScopeAsync();
        await NoConfigurationAsync();
        Arguments();
        CompactGatewayConfigurationCases.Run();
    }

    private static async Task AssessmentAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync();
        string stored = System.IO.File.ReadAllText(environment.File);
        var info = await environment.CallAsync("Info.compatibility", new JsonObject { ["function"] = environment.Name });
        Require(info["status"]?.ToString() == "reusable" && info["revision"]?.ToString() == "1", "unchanged contracts reusable");
        var detail = await environment.CallAsync("Info.result", new JsonObject
        {
            ["evidence_ref"] = info["evidence_ref"]!.ToString(),
            ["section"] = "debug",
            ["limit"] = 8000
        });
        var evidence = JsonNode.Parse(detail["text"]!.ToString())!;
        Require(evidence["acceptance_receipt"]?.ToString() == "false" && evidence["observed"]!["action_schemas"] != null,
            "assessment evidence is explicitly not acceptance and includes actual selected contracts");
        environment.Client.NativeDigest = new('b', 64);
        var changed = await environment.CallAsync("Info.compatibility", new JsonObject { ["function"] = environment.Name });
        Require(changed["status"]?.ToString() == "needs_revalidation" && environment.Client.Mutations.Count == 1
            && System.IO.File.ReadAllText(environment.File) == stored, "assessment has no input or library writes");
        environment.Client.Build = "2.0";
        var differentBuild = await environment.CallAsync("Info.compatibility", new JsonObject { ["function"] = environment.Name });
        Require(differentBuild["status"]?.ToString() == "incompatible", "fresh build change does not reuse cached version");
    }

    private static async Task AcceptedAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync();
        environment.Client.NativeDigest = new('b', 64);
        var refused = await environment.CallAsync(environment.Name, new JsonObject
        {
            ["pid"] = 10,
            ["parameters"] = new JsonObject { ["text"] = "not yet accepted" }
        });
        Require(refused["success"]?.ToString() == "false" && environment.Client.Mutations.Count == 1,
            "changed writer blocks reuse before input");
        var receipt = await environment.IssueSyntheticReceiptAsync();
        var accepted = await environment.CallAsync("Programming.revalidate", environment.Revalidation(receipt));
        Require(accepted["success"]?.ToString() == "true" && accepted["revision"]?.ToString() == "2"
            && accepted["stored_values"]?.ToString() == "false" && environment.Client.Mutations.Count == 1,
            "signed acceptance only updates compatibility atomically");
        var reused = await environment.CallAsync(environment.Name, new JsonObject
        {
            ["pid"] = 10,
            ["parameters"] = new JsonObject { ["text"] = "fresh accepted reuse" }
        });
        Require(reused["success"]?.ToString() == "true" && environment.Client.Field == "fresh accepted reuse",
            "accepted current writer verifies new full field value");
        var stale = await environment.CallAsync("Programming.revalidate", environment.Revalidation(receipt));
        Require(stale["success"]?.ToString() == "false" && stale["error"]?.ToString() == "stale_cartridge_revision",
            "old revision cannot consume a receipt again");
    }

    private static async Task RefusedAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync();
        environment.Client.NativeDigest = new('b', 64);
        string original = System.IO.File.ReadAllText(environment.File);
        var info = await environment.CallAsync("Info.compatibility", new JsonObject { ["function"] = environment.Name });
        var ordinary = new CompactAcceptanceReference(Guid.Parse(info["evidence_ref"]!.ToString()));
        var notReceipt = await environment.CallAsync("Programming.revalidate", environment.Revalidation(ordinary));
        Require(notReceipt["success"]?.ToString() == "false", "result evidence cannot authorize acceptance");
        var receipt = await environment.IssueSyntheticReceiptAsync();
        string file = Path.Combine(environment.Root, "evidence", receipt.Value + ".json");
        var record = JsonNode.Parse(System.IO.File.ReadAllText(file))!;
        record["signature"] = new string('0', 64);
        System.IO.File.WriteAllText(file, record.ToJsonString());
        var forged = await environment.CallAsync("Programming.revalidate", environment.Revalidation(receipt));
        Require(forged["error"]?.ToString() == "forged_acceptance_evidence", "forged private receipt rejected");
        Require(System.IO.File.ReadAllText(environment.File) == original && environment.Client.Mutations.Count == 1,
            "all rejected evidence leaves active body/revision and application unchanged");
    }

    private static async Task ForeignAndChangedScopeAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync();
        environment.Client.NativeDigest = new('b', 64);
        var receipt = await environment.IssueSyntheticReceiptAsync();
        string original = System.IO.File.ReadAllText(environment.File);
        using var foreign = environment.CreateSession(new(Path.Combine(environment.Root, "evidence"), environment.Authority with
        {
            Principal = "foreign-owner"
        }));
        await SelectAsync(foreign);
        var refused = await environment.CallAsync("Programming.revalidate", environment.Revalidation(receipt), foreign);
        Require(refused["error"]?.ToString() == "foreign_or_stale_acceptance_evidence", "host-configured foreign principal rejected");
        environment.Client.NativeDigest = new('c', 64);
        var changed = await environment.CallAsync("Programming.revalidate", environment.Revalidation(receipt));
        Require(changed["success"]?.ToString() == "false" && changed["error"]!.ToString().StartsWith("acceptance_scope_changed"),
            "receipt does not accept a later changed implementation");
        environment.Client.Build = "2.0";
        var incompatible = await environment.CallAsync("Programming.revalidate", environment.Revalidation(receipt));
        Require(incompatible["error"]?.ToString() == "Incompatible function scope cannot be widened by revalidation.",
            "receipt cannot widen application version scope");
        Require(System.IO.File.ReadAllText(environment.File) == original && environment.Client.Mutations.Count == 1,
            "foreign or changed scopes cause no save or input");
    }

    private static async Task NoConfigurationAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        var refused = await environment.CallAsync("Programming.revalidate", environment.Revalidation(new(Guid.NewGuid())));
        Require(refused["success"]?.ToString() == "false" && !Directory.Exists(Path.Combine(environment.Root, "evidence")),
            "gateway never creates missing issuer evidence or signing keys");
        using var readOnly = environment.CreateSession(null, false);
        await SelectAsync(readOnly);
        var info = await environment.CallAsync("Info.compatibility", new JsonObject { ["function"] = environment.Name }, readOnly);
        Require(info["status"]?.ToString() == "reusable", "read-only assessment requires neither learning root nor issuer config");
    }

    private static async Task SelectAsync(CompactGatewaySession session)
    {
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
    }

    private static void Arguments()
    {
        var valid = new JsonObject
        {
            ["function"] = "ui.write",
            ["expected_revision"] = 1,
            ["evidence_ref"] = Guid.NewGuid().ToString()
        };
        Require(CompactCompatibilityCommandCodec.TryRead("Programming.revalidate", valid, out _), "valid typed revalidation arguments");
        foreach (var value in new JsonNode?[] { JsonValue.Create(0), JsonValue.Create(1.5), JsonValue.Create(true),
            JsonValue.Create(9007199254740992L), null })
        {
            var arguments = valid.DeepClone().AsObject();
            arguments["expected_revision"] = value;
            Require(!CompactCompatibilityCommandCodec.TryRead("Programming.revalidate", arguments, out _), "bounded exact revision");
        }
        valid["approved"] = true;
        Require(!CompactCompatibilityCommandCodec.TryRead("Programming.revalidate", valid, out _),
            "caller approval is not receipt evidence");
        valid.Remove("approved");
        valid["evidence_ref"] = "../receipt.json";
        Require(!CompactCompatibilityCommandCodec.TryRead("Programming.revalidate", valid, out _), "receipt ref cannot be a path");
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCompatibilityEnvironment.Require(condition, name);
    }
}
