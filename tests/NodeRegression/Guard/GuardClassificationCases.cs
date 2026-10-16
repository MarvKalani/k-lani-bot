using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;

// The guarded profile must decide explicitly about every tool of the contract. A tool added later without
// a decision used to fall through every check; now it fails this test and is refused at runtime.
internal static class GuardClassificationCases
{
    // Mutating tools that the guarded profile deliberately leaves to the principal's scopes. The backup tools touch no
    // desktop and write only below the operator's fixed archive and recovery roots.
    // app_mcp_call never touches the desktop: its scope is granted only by editing the policy, which also names the programs,
    // and every call passes the backup requirement.
    private static readonly Tool[] MutatingScopeOnlyTools =
        [Tool.FsWriteFile, Tool.SaveMacro, Tool.RunMacro, Tool.EndSession, Tool.BackupCreate, Tool.BackupRestore, Tool.ApplicationMcpCall];

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Guard classification");

        CheckEveryToolClassified(checks);
        GuardFixture fixture = CreateFixture(temporaryRoot);
        CheckProfiles(checks, fixture);
        CheckUnstatedGuard(checks, fixture, temporaryRoot);
        CheckRequiredDecision(checks, fixture);
        CheckTargetProgram(checks, fixture, temporaryRoot);
        CheckApplicationList(checks, temporaryRoot);
        return checks.Passed;
    }

    // A program approved by hash, one rule for its button, and three principals: guarded, desktop and one without settings.
    private sealed record GuardFixture(
        string Application,
        UiGuardOptions Guard,
        PrincipalPolicy Guarded,
        PrincipalPolicy Unrestricted,
        PrincipalPolicy Unstated,
        NodePolicy Policy,
        string Workspace)
    {
        public FakeUi Ui
        {
            get;
        } = new();
    }

    private static bool DeniedPolicy(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static bool Denied(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void CheckEveryToolClassified(Checks checks)
    {
        var contract = ToolContract.Tools.OfType<JsonObject>().ToDictionary(
            tool => ToolNames.Parse(tool["name"]!.ToString()),
            tool => tool["mutates"]!.GetValue<bool>());
        var classified = UiActionGuard.AllClassifications;
        checks.That(
            contract.Keys.All(classified.ContainsKey),
            "every tool of the contract is classified ("
                + string.Join(", ", contract.Keys.Where(name => !classified.ContainsKey(name)))
                + ")");
        checks.That(
            classified.Keys.All(contract.ContainsKey),
            "no classification names a tool that does not exist ("
                + string.Join(", ", classified.Keys.Where(name => !contract.ContainsKey(name)))
                + ")");
        var mutatingScopeOnly = contract.Where(tool => tool.Value && classified[tool.Key] == GuardClassification.ScopeOnly)
            .Select(tool => tool.Key)
            .Order()
            .ToArray();
        checks.That(
            mutatingScopeOnly.SequenceEqual(MutatingScopeOnlyTools.Order()),
            "a mutating tool is left to scopes only by a deliberate decision: " + string.Join(", ", mutatingScopeOnly));
        const Tool ToolAddedLater = (Tool)999;
        checks.That(Denied(() => UiActionGuard.Classify(ToolAddedLater)), "an unclassified tool is refused");
    }

    private static GuardFixture CreateFixture(string temporaryRoot)
    {
        string application = Path.Combine(temporaryRoot, "approved-application");
        File.WriteAllText(application, "synthetic application bytes");
        string applicationHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(application)));
        var rule = new UiActionRule
        {
            Application = application,
            ApplicationSha256 = applicationHash,
            WindowTitle = "Fixture",
            Role = "Button",
            Name = "button",
            Actions = [Tool.ClickElement]
        };
        var guard = new UiGuardOptions { Mode = UiGuardMode.Guarded, Intent = "Synthetic approved task", Rules = [rule] };
        var guarded = new PrincipalPolicy("guarded", new string('a', 64), [Scope.Ui], [], DateTimeOffset.UtcNow.AddHours(1))
        {
            UiGuard = guard
        };
        var unrestricted = new PrincipalPolicy("desktop", new string('b', 64), [Scope.Ui], [], DateTimeOffset.UtcNow.AddHours(1))
        {
            UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
        };
        var unstated = new PrincipalPolicy("unstated", new string('c', 64), [Scope.Ui], [], DateTimeOffset.UtcNow.AddHours(1));
        string workspace = Path.Combine(temporaryRoot, "guard-workspace");
        Directory.CreateDirectory(workspace);
        var policy = new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            WorkspaceRoot = workspace,
            StateRoot = Path.Combine(temporaryRoot, "guard-state"),
            Principals = [guarded, unrestricted, unstated]
        };
        return new GuardFixture(application, guard, guarded, unrestricted, unstated, policy, workspace);
    }

    private static void CheckProfiles(Checks checks, GuardFixture fixture)
    {
        foreach (var (name, classification) in UiActionGuard.AllClassifications)
        {
            bool deniedWithoutArguments = Denied(() =>
                UiActionGuard.Authorize(fixture.Policy, fixture.Guarded, fixture.Ui, name, new JsonObject()));
            bool expectedDenied = classification != GuardClassification.ScopeOnly
                && classification != GuardClassification.FilteredApplicationList;
            checks.That(deniedWithoutArguments == expectedDenied, "guarded behaviour of " + name + " matches " + classification);
            checks.That(
                !Denied(() => UiActionGuard.Authorize(fixture.Policy, fixture.Unrestricted, fixture.Ui, name, new JsonObject())),
                "the desktop profile does not use the guard: " + name);
        }
    }

    // The broad desktop profile is an explicit choice of the operator. A principal that states no guard
    // settings gets the guarded profile, so it can reach no application until rules name one.
    private static void CheckUnstatedGuard(Checks checks, GuardFixture fixture, string temporaryRoot)
    {
        bool hotkeyDenied = Denied(() =>
            UiActionGuard.Authorize(fixture.Policy, fixture.Unstated, fixture.Ui, Tool.Hotkey, new JsonObject()));
        checks.That(hotkeyDenied, "no guard settings means guarded");
        checks.That(fixture.Unstated.UiGuard.Mode == UiGuardMode.Guarded, "the guard settings of a principal default to guarded");

        string nullGuardPolicy = Path.Combine(temporaryRoot, "null-guard-policy.json");
        File.WriteAllText(nullGuardPolicy, new JsonObject
        {
            ["workspaceRoot"] = fixture.Workspace,
            ["stateRoot"] = Path.Combine(temporaryRoot, "null-guard-state"),
            ["principals"] = new JsonArray(new JsonObject
            {
                ["id"] = "explicit-null",
                ["tokenSha256"] = new string('d', 64),
                ["scopes"] = new JsonArray("ui"),
                ["applications"] = new JsonArray(),
                ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
                ["uiGuard"] = null
            })
        }.ToJsonString());
        checks.That(DeniedPolicy(() => NodePolicy.Load(nullGuardPolicy)), "an explicit null uiGuard is refused");
    }

    // A rule can demand the agent's recorded decision for every action on its control, also outside a modal dialog.
    private static void CheckRequiredDecision(Checks checks, GuardFixture fixture)
    {
        var target = new UiTargetIdentity(Environment.ProcessId, fixture.Application, "Fixture", "Button", "button", "", true, true);
        UiActionRule rule = fixture.Guard.Rules[0];
        rule.RequiresDecision = true;
        try
        {
            checks.That(
                Denied(() => UiActionGuard.AuthorizeTarget(fixture.Policy, fixture.Guard, target, Tool.ClickElement, new JsonObject())),
                "a control that requires a decision is refused without one");
            var decided = new JsonObject
            {
                ["decision"] = new JsonObject
                {
                    ["severity"] = "heavy",
                    ["occasion"] = "Synthetic delete button",
                    ["choice"] = "Delete the fixture record",
                    ["reason"] = "The task asks for it",
                    ["expected_result"] = "The record list is one shorter"
                }
            };
            UiActionGuard.AuthorizeTarget(fixture.Policy, fixture.Guard, target, Tool.ClickElement, decided);
            checks.That(true, "the same control is allowed with a recorded decision");
        }
        finally
        {
            rule.RequiresDecision = false;
        }
    }

    // Only a program some rule names is hashed. The old code opened the file first, so a program that
    // is missing or unreadable raised an unrelated IOException instead of a denial.
    private static void CheckTargetProgram(Checks checks, GuardFixture fixture, string temporaryRoot)
    {
        var unnamed = new UiTargetIdentity(
            Environment.ProcessId,
            Path.Combine(temporaryRoot, "does-not-exist"),
            "Fixture",
            "Button",
            "button",
            "",
            true,
            true);
        checks.That(
            Denied(() => UiActionGuard.AuthorizeTarget(fixture.Policy, fixture.Guard, unnamed, Tool.ClickElement, new JsonObject())),
            "a program no rule names is denied without being read");
        var approved = new UiTargetIdentity(Environment.ProcessId, fixture.Application, "Fixture", "Button", "button", "", true, true);
        UiActionGuard.AuthorizeTarget(fixture.Policy, fixture.Guard, approved, Tool.ClickElement, new JsonObject());
        checks.That(true, "the approved program, window and element are accepted");
        File.WriteAllText(fixture.Application, "replaced application bytes");
        checks.That(
            Denied(() => UiActionGuard.AuthorizeTarget(fixture.Policy, fixture.Guard, approved, Tool.ClickElement, new JsonObject())),
            "a replaced program loses its authorization");
        File.Delete(fixture.Application);
        checks.That(
            Denied(() => UiActionGuard.AuthorizeTarget(fixture.Policy, fixture.Guard, approved, Tool.ClickElement, new JsonObject())),
            "an approved program that cannot be read is denied");
    }

    private static UiActionRule RuleFor(string path, string hash)
    {
        return new UiActionRule
        {
            Application = path,
            ApplicationSha256 = hash,
            WindowTitle = "Fixture",
            Role = "Button",
            Actions = [Tool.ClickElement]
        };
    }

    private static void CheckApplicationList(Checks checks, string temporaryRoot)
    {
        using var current = Process.GetCurrentProcess();
        string ownExecutable = current.MainModule!.FileName;
        string ownHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ownExecutable)));
        var onlyOther = new UiGuardOptions
        {
            Mode = UiGuardMode.Guarded,
            Rules = [RuleFor(Path.Combine(temporaryRoot, "other"), new string('c', 64))]
        };
        var ownApproved = new UiGuardOptions { Mode = UiGuardMode.Guarded, Rules = [RuleFor(ownExecutable, ownHash)] };
        var ownWrongHash = new UiGuardOptions { Mode = UiGuardMode.Guarded, Rules = [RuleFor(ownExecutable, new string('d', 64))] };

        checks.That(
            !UiActionGuard.AllowsApplication(onlyOther, Environment.ProcessId),
            "a window of an unapproved program is dropped from the list");
        checks.That(UiActionGuard.AllowsApplication(ownApproved, Environment.ProcessId),
            "a window of the approved program stays in the list");
        checks.That(
            !UiActionGuard.AllowsApplication(ownWrongHash, Environment.ProcessId),
            "a program with a different hash is dropped from the list");
        checks.That(!UiActionGuard.AllowsApplication(ownApproved, int.MaxValue), "a process that does not exist is not approved");
    }
}
