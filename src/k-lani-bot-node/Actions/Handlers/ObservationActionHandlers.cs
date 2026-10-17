using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KLaniBot.Platforms;

namespace KLaniBot.Actions.Handlers;

// Use case: an AI looks at a program before it acts: the element tree, the chapters of a large window,
// a search for one element, what changed since the last look, a screenshot, a check against an expected
// state. These handlers change nothing on the machine. What they return is data from another program,
// so it is marked untrusted, and every reading issues a new snapshot that makes older references stale.
internal sealed class ObservationActionHandlers
{
    private const int MaximumChangedLines = 200;
    private const int MaximumLineLength = 1024;
    private const int MaximumScreenshotBytes = 2 * 1024 * 1024;
    // The only accepted row_format value; the schema in contracts/tools.json declares it.
    private const string CommonRowFormatName = "common";

    private readonly HandlerServices _services;

    public ObservationActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.GetUiTree, GetUiTree);
        add(Tool.GetChapters, GetChapters);
        add(Tool.InspectSection, InspectSection);
        add(Tool.FindElements, FindElements);
        add(Tool.GetUiDelta, GetUiDelta);
        add(Tool.TakeScreenshot, TakeScreenshot);
        add(Tool.WaitElement, WaitForElement);
        add(Tool.VerifyElement, VerifyElement);
    }

    private object GetUiTree(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        WindowSelectionRequest request = WindowActionHandlers.ReadWindowRequest(arguments);
        int maxDepth = arguments["max_depth"]?.GetValue<int>() ?? 4;
        string tree = ReadUiTree(arguments, request, maxDepth);
        if (tree.StartsWith("error:"))
        {
            return TreeFailure(tree);
        }
        string snapshot = _services.Desktop.IssueSnapshot();
        _services.Desktop.RememberObservation(snapshot, tree);
        int limit = arguments["max_nodes"]?.GetValue<int>() ?? 500;
        string[] lines = tree.Split('\n');
        var result = new
        {
            success = true,
            state_fingerprint = _services.Atlas.Observe(_services.Ui.Platform, tree),
            snapshot_id = snapshot,
            tree = string.Join("\n", lines.Take(limit)),
            truncated = lines.Length > limit,
            total_lines = lines.Length,
            data_trust = "untrusted"
        };
        if (arguments["field_metadata"]?.GetValue<bool>() != true)
        {
            return result;
        }

        var response = JsonSerializer.SerializeToNode(result)!.AsObject();
        response["form_metadata"] = JsonSerializer.SerializeToNode(ReadFieldMetadata(lines.Take(limit)));
        return response;
    }

    // A window without any accessibility tree gets its own code, so the last-resort input route can tell it from other failures.
    private static JsonObject TreeFailure(string error)
    {
        var failure = new JsonObject
        {
            ["success"] = false,
            ["error"] = error
        };
        if (error == UiTreeErrors.NoTreeLine)
        {
            failure["code"] = ActionErrorCode.NoAccessibilityTree.ToWireName();
        }

        return failure;
    }

    // Only an explicit row_format request changes the text; other callers keep each backend's own tree.
    private string ReadUiTree(JsonObject arguments, WindowSelectionRequest request, int maxDepth)
    {
        bool commonRows = arguments["row_format"]?.ToString() == CommonRowFormatName;

        return commonRows
            ? _services.Ui.GetCommonUiTree(request, maxDepth)
            : _services.Ui.GetUiTree(request, maxDepth);
    }

    private Dictionary<int, Platforms.UiFieldMetadata> ReadFieldMetadata(IEnumerable<string> lines)
    {
        var result = new Dictionary<int, Platforms.UiFieldMetadata>();

        foreach (string line in lines)
        {
            var match = Regex.Match(line, @"^\s*\[(\d+)\] (Edit|combo box|list box|CheckBox|spin button) ");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
            {
                result.Add(id, _services.Ui.ReadFieldMetadata(id));
            }
        }

        return result;
    }

    private object GetChapters(ActionCall call)
    {
        int? pid = call.Arguments["pid"]?.GetValue<int?>();
        string? title = call.Arguments["title"]?.ToString();
        string chapters = _services.Ui.GetChapters(pid, title);
        string chapterSnapshot = _services.Desktop.IssueChapterSnapshot(pid);
        if (chapters.StartsWith("error:"))
        {
            return new
            {
                success = false,
                error = chapters
            };
        }
        return new
        {
            success = true,
            chapters,
            snapshot_id = chapterSnapshot,
            data_trust = "untrusted"
        };
    }

    private object InspectSection(ActionCall call)
    {
        string chapterId = call.Arguments["chapter_id"]?.ToString() ?? "K1";
        int maxDepth = call.Arguments["max_depth"]?.GetValue<int>() ?? 3;
        string sectionTree = _services.Ui.InspectSection(chapterId, maxDepth);
        string snapshot = _services.Desktop.IssueSnapshot();
        if (sectionTree.StartsWith("error:"))
        {
            return new
            {
                success = false,
                error = sectionTree
            };
        }
        return new
        {
            success = true,
            sectionTree,
            snapshot_id = snapshot,
            data_trust = "untrusted"
        };
    }

    // Deep GTK forms keep their fields far below the window; the node cap on visited elements bounds the cost.
    private const int SearchObservationDepth = 32;

    // Each tree level costs two JSON levels (the Children array and the element object), so the default limit of 64 would
    // reject a tree read to the search depth; the extra levels cover the root and wrapper objects.
    private const int SerializedLevelsPerTreeLevel = 2;
    private const int SerializedWrapperLevels = 16;

    private static readonly JsonSerializerOptions DeepTreeOptions = new()
    {
        MaxDepth = SerializedLevelsPerTreeLevel * SearchObservationDepth * 2 + SerializedWrapperLevels
    };

    // Searching waits only for an element to appear, or for an exact match count when the caller names one.
    // It never retries anything that changes the machine, and without a count it does not wait an
    // ambiguous search down to its first remaining match.
    private object FindElements(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        var filter = new ElementFilter(arguments);
        ElementFilter? containerFilter = arguments["within"] is JsonObject within ? new ElementFilter(within) : null;
        var matches = new JsonArray();
        int containerMatches = 0;
        var waiting = Stopwatch.StartNew();
        int timeoutMilliseconds = arguments["timeout_ms"]?.GetValue<int>() ?? 0;
        int? expectedCount = arguments["count"]?.GetValue<int>();
        string snapshot;
        while (true)
        {
            _services.RequireApproval(call.Caller);
            int? pid = arguments["pid"]?.GetValue<int?>();
            object? tree = _services.Ui.GetVisualTree(pid, arguments["title"]?.ToString(), SearchObservationDepth);
            snapshot = _services.Desktop.IssueSnapshot();
            matches.Clear();
            JsonNode? treeNode = JsonSerializer.SerializeToNode(tree, DeepTreeOptions);
            containerMatches = CollectSearchMatches(treeNode, filter, containerFilter, matches);
            if (SearchIsSettled(matches.Count, expectedCount) || waiting.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                break;
            }
            Thread.Sleep(50);
        }
        if (containerFilter != null && containerMatches != 1)
        {
            return BuildContainerFailure(containerMatches, snapshot);
        }
        return BuildFindResult(arguments, matches, snapshot, expectedCount);
    }

    // Without a container the whole tree is searched. With one, the search covers only the descendants of the single
    // element matching it; the returned number tells the caller how many containers matched.
    private static int CollectSearchMatches(JsonNode? tree, ElementFilter filter, ElementFilter? containerFilter, JsonArray matches)
    {
        if (containerFilter == null)
        {
            CollectMatches(tree, filter, matches);
            return 0;
        }
        var containers = new List<JsonObject>();
        CollectContainers(tree, containerFilter, containers);
        if (containers.Count == 1)
        {
            CollectMatches(containers[0]["Children"], filter, matches);
        }
        return containers.Count;
    }

    private static void CollectContainers(JsonNode? node, ElementFilter containerFilter, List<JsonObject> containers)
    {
        if (node is JsonObject element)
        {
            if (containerFilter.Matches(element))
            {
                containers.Add(element);
            }
            foreach (var property in element)
            {
                CollectContainers(property.Value, containerFilter, containers);
            }
        }
        else if (node is JsonArray items)
        {
            foreach (JsonNode? item in items)
            {
                CollectContainers(item, containerFilter, containers);
            }
        }
    }

    private static JsonObject BuildContainerFailure(int containerMatches, string snapshot)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["error"] = "Container is absent or ambiguous.",
            ["container_matches"] = containerMatches,
            ["snapshot_id"] = snapshot,
            ["data_trust"] = "untrusted"
        };
    }

    private static JsonObject BuildFindResult(JsonObject arguments, JsonArray matches, string snapshot, int? expectedCount)
    {
        int count = matches.Count;
        int limit = arguments["limit"]?.GetValue<int>() ?? 50;
        while (matches.Count > limit)
        {
            matches.RemoveAt(matches.Count - 1);
        }
        var result = new JsonObject
        {
            ["success"] = true,
            ["snapshot_id"] = snapshot,
            ["matches"] = matches,
            ["total_matches"] = count,
            ["ambiguous"] = count != 1,
            ["truncated"] = count > limit,
            ["data_trust"] = "untrusted"
        };
        if (expectedCount != null)
        {
            result["count_reached"] = count == expectedCount;
        }
        return result;
    }

    // With a requested count the search is settled only on that exact count, so a list that is still
    // filtering is not reported early; without one, any match ends the wait.
    private static bool SearchIsSettled(int matchCount, int? expectedCount)
    {
        return expectedCount == null ? matchCount > 0 : matchCount == expectedCount;
    }

    private static void CollectMatches(JsonNode? node, ElementFilter filter, JsonArray matches)
    {
        if (node is JsonObject element)
        {
            if (filter.Matches(element))
            {
                matches.Add(CopyWithoutChildren(element));
            }
            foreach (var property in element)
            {
                CollectMatches(property.Value, filter, matches);
            }
        }
        else if (node is JsonArray items)
        {
            foreach (JsonNode? item in items)
            {
                CollectMatches(item, filter, matches);
            }
        }
    }

    private static JsonObject CopyWithoutChildren(JsonObject element)
    {
        var copy = new JsonObject();
        foreach (var property in element.Where(property => property.Key != "Children"))
        {
            copy[property.Key] = property.Value?.DeepClone();
        }
        return copy;
    }

    // The criteria of a search: text fields match ignoring case (name_contains matches a part), flags must be equal. A criterion that was
    // not given matches everything.
    private sealed class ElementFilter(JsonObject criteria)
    {
        public bool Matches(JsonObject element)
        {
            return element["Id"] != null
                && TextMatches(element, "name", "Name")
                && NameContains(element)
                && TextMatches(element, "auto_id", "AutoId")
                && TextMatches(element, "role", "Role")
                && FlagMatches(element, "visible", "IsOffscreen", true)
                && FlagMatches(element, "enabled", "IsEnabled", false);
        }

        private bool TextMatches(JsonObject element, string criterion, string field)
        {
            if (criteria[criterion] == null)
            {
                return true;
            }
            return element[field]?.ToString().Equals(criteria[criterion]!.ToString(), StringComparison.OrdinalIgnoreCase) == true;
        }

        private bool NameContains(JsonObject element)
        {
            if (criteria["name_contains"] == null)
            {
                return true;
            }
            string part = criteria["name_contains"]!.ToString();
            return element["Name"]?.ToString().Contains(part, StringComparison.OrdinalIgnoreCase) == true;
        }

        private bool FlagMatches(JsonObject element, string criterion, string field, bool inverted)
        {
            if (criteria[criterion] == null)
            {
                return true;
            }
            if (element[field] is not JsonValue value || !value.TryGetValue<bool>(out bool actual))
            {
                return false;
            }
            return criteria[criterion]!.GetValue<bool>() == (inverted ? !actual : actual);
        }
    }

    // What changed since an earlier reading, compared without the element numbers, which differ from
    // reading to reading. Without the earlier reading the client is told to look again in full.
    private object GetUiDelta(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        int? pid = arguments["pid"]?.GetValue<int?>();
        string? title = arguments["title"]?.ToString();
        int maxDepth = arguments["max_depth"]?.GetValue<int>() ?? 4;
        string tree = _services.Ui.GetUiTree(new WindowSelectionRequest(pid, title, null), maxDepth);
        if (tree.StartsWith("error:"))
        {
            return new
            {
                success = false,
                error = tree
            };
        }
        string previous = arguments["since_snapshot"]?.ToString() ?? "";
        string snapshot = _services.Desktop.IssueSnapshot();
        bool baseAvailable = _services.Desktop.TryGetObservation(previous, out string earlierTree);
        string[] before = baseAvailable ? WithoutElementNumbers(earlierTree).Split('\n') : [];
        string[] after = WithoutElementNumbers(tree).Split('\n');
        _services.Desktop.RememberObservation(snapshot, tree);
        string[] added = baseAvailable ? after.Except(before).ToArray() : [];
        string[] removed = baseAvailable ? before.Except(after).ToArray() : [];
        bool truncated = added.Length > MaximumChangedLines
            || removed.Length > MaximumChangedLines
            || added.Concat(removed).Any(line => line.Length > MaximumLineLength);
        return new
        {
            success = true,
            snapshot_id = snapshot,
            base_available = baseAvailable,
            requires_full_observation = !baseAvailable,
            added = added.Take(MaximumChangedLines).Select(ClipLine).ToArray(),
            removed = removed.Take(MaximumChangedLines).Select(ClipLine).ToArray(),
            truncated,
            data_trust = "untrusted"
        };
    }

    private static string WithoutElementNumbers(string tree)
    {
        return Regex.Replace(tree, @"(?m)^(\s*(?:- )?)\[\d+\]", "$1[ref]");
    }

    private static string ClipLine(string line)
    {
        return line.Length > MaximumLineLength ? line[..MaximumLineLength] : line;
    }

    private object TakeScreenshot(ActionCall call)
    {
        var (ok, message, png) = _services.Ui.Screenshot();
        if (ok && (png == null || png.Length > MaximumScreenshotBytes))
        {
            return new
            {
                success = false,
                code = ActionErrorCode.ImageTooLarge.ToWireName(),
                error = "Screenshot exceeds the bounded image transfer."
            };
        }
        return new
        {
            success = ok,
            message,
            image = ok ? Convert.ToBase64String(png!) : null,
            mime_type = "image/png"
        };
    }

    private object WaitForElement(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        string? title = arguments["title"]?.ToString() ?? arguments["window_title"]?.ToString();
        var (ok, message, element) = _services.Ui.WaitForElement(
            arguments["name"]?.ToString(),
            arguments["auto_id"]?.ToString(),
            arguments["role"]?.ToString(),
            arguments["pid"]?.GetValue<int?>(),
            title,
            arguments["state"]?.ToString() ?? "exists",
            arguments["timeout_ms"]?.GetValue<int?>() ?? 10000);
        return new
        {
            success = ok,
            message,
            element,
            snapshot_id = _services.Desktop.IssueSnapshot()
        };
    }

    // The tool's own defaults differ from the postcondition check in the dispatcher: here an element is
    // expected to be enabled unless the caller says otherwise.
    private object VerifyElement(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        string expectedState = arguments["expected_state"]?.ToString() ?? arguments["state"]?.ToString() ?? "enabled";
        string? expectedText = arguments["expected_text"]?.ToString() ?? arguments["text"]?.ToString();
        var (ok, message, verification) = _services.Ui.VerifyElement(
            arguments["id"]?.GetValue<int?>(),
            arguments["name"]?.ToString(),
            arguments["auto_id"]?.ToString(),
            arguments["role"]?.ToString(),
            arguments["pid"]?.GetValue<int?>(),
            arguments["window_title"]?.ToString(),
            expectedState,
            expectedText,
            arguments["timeout_ms"]?.GetValue<int?>() ?? 3000);
        return new
        {
            success = ok,
            message,
            verification
        };
    }
}
