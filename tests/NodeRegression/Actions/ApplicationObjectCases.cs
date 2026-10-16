using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Platforms;

// Native application calls keep the node's authority boundary. Invalid item bindings and field types must stop before
// backend dispatch; verified native results and untrusted reads retain their actual meaning on the wire.
internal static class ApplicationObjectCases
{
    public static int Run(string root)
    {
        var checks = new Checks("Application objects");
        var ui = new FakeUi();
        ui.ExtraFeatures.Add(ToolFeature.ApplicationObjects);
        using var dispatcher = new ActionDispatcher(ui, Policy(root));
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        JsonObject info = Call(dispatcher, caller, "inspect_application", new JsonObject { ["pid"] = 100, ["area"] = "Info" });
        checks.That(info["success"]!.GetValue<bool>() && info["info"]!["Build"]!.ToString() == "16.0.1.2",
            "native identity is returned with separate program build and lowercase outcome envelope");
        checks.That(info["data_trust"]!.ToString() == "untrusted" && ui.NativeReads == 1, "native reads remain external data");
        CheckProgramStatus(checks, dispatcher, caller, ui);
        CheckMissingBindings(checks, dispatcher, caller, ui);
        CheckFieldTypes(checks, dispatcher, caller, ui);
        CheckVerifiedResult(checks, dispatcher, caller, ui);
        CheckMailBindings(checks, dispatcher, caller, ui);
        CheckApplicationThread(checks);
        CheckCompactMail(checks);
        CheckReadableHtml(checks);
        CheckFolderBindings(checks, dispatcher, caller, ui);

        return checks.Passed;
    }

    private static void CheckReadableHtml(Checks checks)
    {
        string text = MailHtmlText.Read("<html><head><style>.a{color:red}</style></head><body>"
            + "<div style='display:none'>Hidden advertisement</div><p>Invoice &amp; receipt</p>"
            + "<table><tr><td>Amount</td><td>75.00 EUR</td></tr></table>"
            + "<script>run()</script><img src='tracking.gif'><a href='https://fixture.example/?secret=x'>Download invoice</a>"
            + "</body></html>");

        checks.That(text.Contains("75.00 EUR", StringComparison.Ordinal) && text.Contains("Invoice & receipt", StringComparison.Ordinal),
            "HTML conversion preserves invoice text, entities and table amounts");
        checks.That(!text.Contains("Hidden advertisement", StringComparison.Ordinal) && !text.Contains("run()", StringComparison.Ordinal)
            && !text.Contains("secret", StringComparison.Ordinal), "HTML styling, hidden preheaders, scripts and link targets stay out");
        checks.That(ApplicationMailText.Readable("A\u200c\u034f\u00ad B\n\n\nC <https://fixture.example/token>") == "A B\n\nC",
            "review text contains no invisible padding, repeated blank lines or angle-bracket URL targets");
    }

    private static void CheckFolderBindings(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        int writes = ui.NativeWrites;
        JsonObject result = Call(dispatcher, caller, "operate_application", new JsonObject
        {
            ["pid"] = 100,
            ["operation"] = "CreateFolder",
            ["folder_name"] = "Werbung"
        });

        checks.That(result["outcome"]!.ToString() == "not_dispatched" && ui.NativeWrites == writes,
            "folder creation without an observed parent and store cannot reach the backend");
    }

    private static void CheckCompactMail(Checks checks)
    {
        const string Prose = "Invoice due tomorrow: 75 EUR.";
        string original = Prose + "\r\n<https://fixture.example/invoice.pdf?tracking=" + new string('x', 5000) + ">";
        string compact = ApplicationMailText.Compact(original);

        checks.That(compact.Contains(Prose, StringComparison.Ordinal)
            && compact.Contains("fixture.example/invoice.pdf", StringComparison.Ordinal) && compact.Length < 100,
            "compact mail preserves payment prose and link purpose while omitting verbose query parameters");
        checks.That(ApplicationMailText.Compact("A < B and 5 > 3") == "A < B and 5 > 3",
            "ordinary prose with angle brackets is not mistaken for a link target");
    }

    private static void CheckMailBindings(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        int reads = ui.NativeReads;
        JsonObject missingFolder = Call(dispatcher, caller, "inspect_application", new JsonObject
        {
            ["pid"] = 100,
            ["area"] = "MailPage"
        });
        JsonObject missingStore = Call(dispatcher, caller, "inspect_application", new JsonObject
        {
            ["pid"] = 100,
            ["area"] = "Mail",
            ["entry_id"] = "mail"
        });

        checks.That(!missingFolder["success"]!.GetValue<bool>() && !missingStore["success"]!.GetValue<bool>()
            && ui.NativeReads == reads, "mail pages and body reads reject incomplete identities before dispatch");
    }

    private static void CheckApplicationThread(Checks checks)
    {
        int callerThread = Environment.CurrentManagedThreadId;
        int officeThread = callerThread;
        ApplicationObjectResult result = ApplicationObjectThread.Run(() =>
        {
            officeThread = Environment.CurrentManagedThreadId;
            return new(true, "Detached result");
        });
        int executions = 0;
        ApplicationObjectResult failure = ApplicationObjectThread.Run(() =>
        {
            executions++;
            throw new InvalidOperationException("Synthetic Office refusal");
        });

        checks.That(result.Success && officeThread != callerThread, "Office access does not inherit the UI dispatch context");
        checks.That(!failure.Success && executions == 1, "Office failures return without retrying a possibly dispatched mutation");
    }

    private static void CheckProgramStatus(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        JsonObject absentProcess = Call(dispatcher, caller, "inspect_application", new JsonObject { ["area"] = "Selection" });
        checks.That(absentProcess["success"]!.GetValue<bool>() == false && ui.NativeReads == 1,
            "a mail read without a selected process stops before native access");
        JsonObject status = Call(dispatcher, caller, "inspect_application", new JsonObject { ["area"] = "Status" });
        checks.That(status["success"]!.GetValue<bool>() && status["status"]!["Running"]!.GetValue<bool>() && ui.NativeReads == 2,
            "native program status needs no running window or preselected process");
    }

    private static void CheckMissingBindings(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        JsonObject result = Call(dispatcher, caller, "operate_application", new JsonObject
        {
            ["pid"] = 100,
            ["operation"] = "Move",
            ["destination_folder_id"] = "target"
        });

        checks.That(result["outcome"]!.ToString() == "not_dispatched" && ui.NativeWrites == 0,
            "move without a saved item and expected source never reaches the native backend");
    }

    private static void CheckFieldTypes(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        JsonObject arguments = BoundCommand("SetField");
        arguments["field"] = "Subject";
        arguments["value"] = new JsonObject { ["boolean"] = true };
        JsonObject result = Call(dispatcher, caller, "operate_application", arguments);
        checks.That(result["outcome"]!.ToString() == "not_dispatched" && ui.NativeWrites == 0,
            "a subject cannot accept a boolean through a generic property setter");
        arguments["field"] = "Importance";
        arguments["value"] = new JsonObject { ["integer"] = 3 };
        result = Call(dispatcher, caller, "operate_application", arguments);
        checks.That(result["outcome"]!.ToString() == "not_dispatched" && ui.NativeWrites == 0,
            "the importance enum rejects the sensitivity-only value three");
    }

    private static void CheckVerifiedResult(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        JsonObject arguments = BoundCommand("SetField");
        arguments["field"] = "Subject";
        arguments["value"] = new JsonObject { ["text"] = "Synthetic draft" };
        JsonObject result = Call(dispatcher, caller, "operate_application", arguments);

        checks.That(ui.NativeWrites == 1 && result["outcome"]!.ToString() == "verified",
            "a verified native property write is not reported as merely dispatched");
        checks.That(result["mutation"]!["EntryId"]!.ToString() == "new-id", "fresh native item identities reach the caller");
    }

    private static JsonObject BoundCommand(string operation)
    {
        return new JsonObject
        {
            ["pid"] = 100,
            ["operation"] = operation,
            ["entry_id"] = "item",
            ["store_id"] = "store",
            ["expected_folder_id"] = "folder"
        };
    }

    private static JsonObject Call(ActionDispatcher dispatcher, RequestContext caller, string action, JsonObject arguments)
    {
        if (action == "operate_application")
        {
            arguments["request_id"] = Guid.CreateVersion7().ToString();
        }

        return dispatcher.Execute(new JsonObject { ["action"] = action, ["args"] = arguments.DeepClone() }, caller);
    }

    private static NodePolicy Policy(string root)
    {
        string state = Path.Combine(root, "native-object-state");
        string workspace = Path.Combine(root, "native-object-workspace");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(workspace);

        return new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            StateRoot = state,
            WorkspaceRoot = workspace,
            Principals = [new PrincipalPolicy("operator", new string('a', 64), [Scope.Observe, Scope.Ui], [],
                DateTimeOffset.UtcNow.AddHours(1)) { UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop } }]
        };
    }
}
