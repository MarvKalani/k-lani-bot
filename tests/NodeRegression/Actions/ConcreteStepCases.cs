using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Localization;
using KLaniBot.Visibility;

// An operator must see the concrete purpose of an observation and its verified result, rather than an opaque feature label.
internal static class ConcreteStepCases
{
    public static int Run()
    {
        var checks = new Checks("Concrete steps");
        UiLanguage previous = UiTexts.Language;
        try
        {
            UiTexts.Use(UiLanguage.German);
            StepView step = StepDescriber.DescribeMacroStep(new JsonObject
            {
                ["action"] = "inspect_application",
                ["args"] = new JsonObject { ["pid"] = 100, ["area"] = "Info" }
            });
            checks.That(step.Detail.Contains("Version", StringComparison.Ordinal),
                "the version query is named as such before it runs, rather than screen reading");
            checks.That(step.Kind == StepKind.Read && step.Title.Length > 0,
                "a concrete action title preserves the step's actual interaction kind");
            CheckIdentityResult(checks);
            CheckMailDetails(checks);
            CheckDisplayLevels(checks);
            CheckEnteredValuesStayPrivate(checks);
            checks.That(StepEfficiencyCases.Run() > 0, "retained efficiency measurements passed");
        }
        finally
        {
            UiTexts.Use(previous);
        }

        return checks.Passed;
    }

    private static void CheckIdentityResult(Checks checks)
    {
        var result = new JsonObject
        {
            ["success"] = true,
            ["info"] = new JsonObject
            {
                ["Variant"] = "Outlook Classic",
                ["Version"] = "16.0",
                ["Build"] = "16.0.14334.20918",
                ["Language"] = "en-US"
            },
            ["mail"] = new JsonObject { ["Body"] = "private message body" }
        };
        string description = StepResultDescriber.Describe(Tool.InspectApplication, result);
        checks.That(description.Contains("16.0.14334.20918", StringComparison.Ordinal)
            && description.Contains("en-US", StringComparison.Ordinal) && description.Contains("Quelle", StringComparison.Ordinal),
            "the actual build, language and source appear beside the completed identity step");
        checks.That(!description.Contains("private message body", StringComparison.Ordinal),
            "an identity result never copies private mail content into the task window");
        result["success"] = false;
        checks.That(StepResultDescriber.Describe(Tool.InspectApplication, result).Length == 0,
            "a failed observation never presents an identity as successfully read");
    }

    private static void CheckMailDetails(Checks checks)
    {
        JsonObject result = MailResult();
        StepDiagnostics details = StepDiagnosticDescriber.Describe(Tool.InspectApplication,
            new JsonObject { ["entry_id"] = "message-1", ["area"] = "Mail" }, result, "operation-1", 123);
        checks.That(details.Summary.Contains("Invoice fixture", StringComparison.Ordinal)
            && details.Summary.Contains("billing@example.invalid", StringComparison.Ordinal),
            "mail summaries identify the actual subject and SMTP sender");
        checks.That(details.Verbose == "Invoice €10\nPlease read.", "verbose contains exactly the returned mail body");
        checks.That(!details.Debug.Contains("Please read.", StringComparison.Ordinal), "debug excludes mail body content");
        checks.That(details.ResultJsonBytes == System.Text.Encoding.UTF8.GetByteCount(result.ToJsonString()),
            "JSON byte counts measure the serialized payload rather than UTF-16 characters");
        checks.That(details.Debug.Contains("ReturnedBodyUtf8Bytes", StringComparison.Ordinal)
            && details.Debug.Contains("message-1", StringComparison.Ordinal), "debug includes body bytes and exact message identity");
        checks.That(details.Summary.Contains("Ausschnitt", StringComparison.Ordinal), "a partial read is not presented as a full message");
    }

    private static void CheckDisplayLevels(Checks checks)
    {
        StepDiagnostics details = StepDiagnosticDescriber.Describe(Tool.InspectApplication, new(), MailResult(), "operation-1", 1);
        var first = new StepView(StepKind.Read, "Read mail", StepStatus.Completed, Diagnostics: details);
        var second = new StepView(StepKind.Read, "Read version", StepStatus.Completed, "Version 16.0");
        var steps = new StepList([first], second, [], false);
        checks.That(StepLogText.Format(steps, StepLogLevel.None) == "", "none exports no step content");
        string standard = StepLogText.Format(steps, StepLogLevel.Standard);
        checks.That(standard.Contains("Invoice fixture", StringComparison.Ordinal)
            && standard.Contains("Version 16.0", StringComparison.Ordinal), "copy all spans multiple step summaries");
        checks.That(!standard.Contains("Please read.", StringComparison.Ordinal), "standard does not reveal the body");
        string debug = StepLogText.Format(steps, StepLogLevel.Debug);
        checks.That(debug.Contains("operation-1", StringComparison.Ordinal)
            && !debug.Contains("Please read.", StringComparison.Ordinal), "debug adds metadata without body content");
        checks.That(StepLogText.Format(steps, StepLogLevel.Verbose).Contains("Please read.", StringComparison.Ordinal),
            "verbose includes the returned text for copying");
    }

    private static void CheckEnteredValuesStayPrivate(Checks checks)
    {
        var arguments = new JsonObject { ["text"] = "private entered value", ["pid"] = 100 };
        StepDiagnostics details = StepDiagnosticDescriber.Describe(Tool.TypeText, arguments, new(), "operation-2", 1);
        checks.That(!details.Debug.Contains("private entered value", StringComparison.Ordinal) && details.Verbose.Length == 0,
            "entered values do not leak into an observation log");
    }

    private static JsonObject MailResult()
    {
        return new JsonObject
        {
            ["success"] = true,
            ["mail"] = new JsonObject
            {
                ["EntryId"] = "message-1",
                ["Subject"] = "Invoice fixture",
                ["SenderAddress"] = "billing@example.invalid",
                ["Body"] = "Invoice €10\nPlease read.",
                ["TextFormat"] = "Readable",
                ["BodyOffset"] = 0,
                ["BodyLength"] = 100,
                ["Truncated"] = true,
                ["ContentSha256"] = "observed-content"
            }
        };
    }
}
