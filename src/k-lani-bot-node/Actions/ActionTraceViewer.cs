using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Localization;

namespace KLaniBot.Actions;

// Writes index.html, the page the indicator opens. Each step is rendered once and kept in memory; a
// new step only renders itself, and steps that left the directory leave the page in the same write,
// so the page never links an image that was rotated away.
internal sealed class ActionTraceViewer
{
    private readonly string _directory;
    private readonly string _rejectedDirectory;
    private readonly Dictionary<string, string> _renderedSteps = new(StringComparer.Ordinal);

    public ActionTraceViewer(string directory, string rejectedDirectory)
    {
        _directory = directory;
        _rejectedDirectory = rejectedDirectory;
    }

    public void Write()
    {
        List<string> executed = StepFiles(_directory);
        List<string> rejected = StepFiles(_rejectedDirectory);
        ForgetRemovedSteps(executed.Concat(rejected));
        string header = Header
            .Replace("{LANGUAGE}", WireNames<UiLanguage>.Of(UiTexts.Language))
            .Replace("{TITLE}", WebUtility.HtmlEncode(UiTexts.Get(UiText.TracePageTitle)));
        var html = new StringBuilder(header);
        html.Append("<h2>" + WebUtility.HtmlEncode(UiTexts.Get(UiText.TraceExecutedSteps)) + "</h2>");
        AppendSteps(html, executed.TakeLast(ActionTrace.ExecutedStepLimit));
        html.Append("<h2>" + WebUtility.HtmlEncode(UiTexts.Get(UiText.TraceRejectedRequests)) + "</h2>");
        AppendSteps(html, rejected.TakeLast(ActionTrace.RejectedStepLimit));
        html.Append("</html>");
        WriteAtomically(html.ToString());
    }

    private static List<string> StepFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private void ForgetRemovedSteps(IEnumerable<string> present)
    {
        var current = new HashSet<string>(present, StringComparer.Ordinal);
        foreach (string path in _renderedSteps.Keys.Where(path => !current.Contains(path)).ToList())
        {
            _renderedSteps.Remove(path);
        }
    }

    private void AppendSteps(StringBuilder html, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!_renderedSteps.TryGetValue(path, out string? rendered))
            {
                rendered = RenderStep(path);
                _renderedSteps[path] = rendered;
            }
            html.Append(rendered);
        }
    }

    private string RenderStep(string path)
    {
        JsonObject step = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonObject receipt = step["receipt"]!.AsObject();
        bool success = receipt["success"]?.GetValue<bool>() == true;
        var html = new StringBuilder();
        html.Append(success ? "<details class=ok>" : "<details class=failed open>");
        html.Append("<summary>");
        html.Append(Escape(receipt["timestamp"]));
        html.Append(" · ");
        html.Append(Escape(receipt["action"]));
        html.Append(" · ");
        html.Append(Escape(receipt["outcome"]));
        html.Append("</summary><dl>");
        AppendDetail(html, UiTexts.Get(UiText.TracePurpose), receipt["purpose"]);
        AppendDetail(html, "Master", receipt["master"]);
        AppendDetail(html, UiTexts.Get(UiText.TraceSession), receipt["session_id"]);
        AppendDetail(html, UiTexts.Get(UiText.TraceOperationId), receipt["operation_id"]);
        AppendDetail(html, UiTexts.Get(UiText.TraceTargetElement), receipt["target_element_id"]);
        AppendDetail(html, UiTexts.Get(UiText.TraceWindowInteraction), receipt["window_interaction"]);
        AppendDetail(html, UiTexts.Get(UiText.TraceError), receipt["error"]);
        if (receipt["decision"] is JsonObject decision)
        {
            AppendDetail(html, UiTexts.Get(UiText.TraceSeverity), decision["severity"]);
            AppendDetail(html, UiTexts.Get(UiText.TraceOccasion), decision["occasion"]);
            AppendDetail(html, UiTexts.Get(UiText.TraceDecision), decision["choice"]);
            AppendDetail(html, UiTexts.Get(UiText.TraceReason), decision["reason"]);
            AppendDetail(html, UiTexts.Get(UiText.TraceExpectedResult), decision["expected_result"]);
            AppendDetail(html, UiTexts.Get(UiText.TraceActualResult), receipt["decision_result"]);
        }
        html.Append("</dl><div class=images>");
        bool executedStep = Path.GetDirectoryName(path) == _directory;
        foreach (string phase in new[] { "before", "after" })
        {
            AppendImage(html, step, receipt, phase, executedStep);
        }
        html.Append("</div></details>");
        return html.ToString();
    }

    // Only a file name that the node itself would have chosen for this step is linked. The JSON is
    // local, but the page must not become a way to show arbitrary files.
    private static void AppendImage(StringBuilder html, JsonObject step, JsonObject receipt, string phase, bool executedStep)
    {
        string expectedName = receipt["operation_id"] + "-" + phase + ".png";
        string? image = step["screenshots"]?[phase]?["file"]?.ToString();
        bool validIdentifier = Guid.TryParse(receipt["operation_id"]?.ToString(), out Guid identifier) && identifier.Version == 7;
        if (executedStep && image == expectedName && validIdentifier)
        {
            html.Append("<figure><figcaption>");
            html.Append(WebUtility.HtmlEncode(UiTexts.Get(phase == "before" ? UiText.TraceBefore : UiText.TraceAfter)));
            html.Append("</figcaption><img alt='" + WebUtility.HtmlEncode(UiTexts.Get(UiText.TraceScreenshot)) + "' loading=lazy src='");
            html.Append(WebUtility.HtmlEncode(image));
            html.Append("'></figure>");
            return;
        }
        string label = UiTexts.Get(phase == "before" ? UiText.TraceBeforeImageMissing : UiText.TraceAfterImageMissing);
        AppendDetail(html, label, step["screenshots"]?[phase + "_unavailable"]);
    }

    private static string Escape(JsonNode? value)
    {
        return WebUtility.HtmlEncode(value?.ToString() ?? "");
    }

    private static void AppendDetail(StringBuilder html, string label, JsonNode? value)
    {
        if (value == null)
        {
            return;
        }
        html.Append("<dt>");
        html.Append(WebUtility.HtmlEncode(label));
        html.Append("</dt><dd>");
        html.Append(Escape(value));
        html.Append("</dd>");
    }

    private void WriteAtomically(string html)
    {
        string temporary = Path.Combine(_directory, Guid.CreateVersion7() + ".tmp");
        try
        {
            using (var stream = PrivateFile.OpenWrite(temporary, FileMode.CreateNew, FileShare.Read))
            {
                stream.Write(Encoding.UTF8.GetBytes(html));
                stream.Flush(true);
            }
            File.Move(temporary, Path.Combine(_directory, "index.html"), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private const string Header = """
        <!doctype html>
        <html lang="{LANGUAGE}">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; img-src 'self'; style-src 'unsafe-inline'">
        <title>{TITLE}</title>
        <style>
            body {
                font: 16px system-ui;
                background: #101820;
                color: #edf4fa;
                margin: 2rem auto;
                max-width: 1100px;
                padding: 0 1rem;
            }
            details {
                background: #1b2934;
                padding: 1rem;
                margin: 1rem 0;
                border-radius: 8px;
            }
            summary { cursor: pointer; font-weight: 600; }
            .images { display: flex; gap: 1rem; flex-wrap: wrap; }
            figure { margin: 0; flex: 1; min-width: 250px; }
            img { width: 100%; height: auto; }
            dt { color: #adc3d5; margin-top: .6rem; }
            dd { margin-left: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
            .failed { border-left: 4px solid #ff6262; }
            .ok { border-left: 4px solid #63cb9a; }
        </style>
        <h1>k-lani-bot · Ablaufprotokoll</h1>
        <p>Lokale Aufzeichnung. Bilder können persönliche Daten enthalten.
           Ein gesendeter Befehl ist noch kein bestätigtes Ergebnis.</p>
        """;
}
