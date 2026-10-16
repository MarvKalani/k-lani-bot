using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Platforms;

namespace KLaniBot.Actions.Handlers;

// Native application objects let a cartridge read and change data without shared pointer or keyboard input.
// The dispatcher retains scopes, consent, pacing, guard, journal and operation identity for every call.
internal sealed class ApplicationObjectActionHandlers(HandlerServices services)
{
    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.InspectApplication, Inspect);
        add(Tool.OperateApplication, Operate);
    }

    private object Inspect(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        var query = new ApplicationObjectQuery(arguments["pid"]?.GetValue<int>() ?? 0,
            Enum.Parse<ApplicationReadArea>(arguments["area"]?.ToString() ?? "Info"),
            arguments["type_name"]?.ToString() ?? "", Identity(arguments, "folder_id"), Identity(arguments, "store_id"),
            arguments["offset"]?.GetValue<int>() ?? 0, arguments["limit"]?.GetValue<int>() ?? 20,
            arguments["max_characters"]?.GetValue<int>() ?? 32768, Identity(arguments, "entry_id"),
            arguments["body_offset"]?.GetValue<int>() ?? 0,
            Enum.Parse<ApplicationMailTextFormat>(arguments["text_format"]?.ToString() ?? "Raw"));

        if ((query.Area != ApplicationReadArea.Status && query.ProcessId <= 0)
            || (query.FolderId != null && query.StoreId == null)
            || (query.Area == ApplicationReadArea.MailPage && query.FolderId == null)
            || (query.Area == ApplicationReadArea.Mail && (query.EntryId == null || query.StoreId == null)))
        {
            return new
            {
                success = false,
                code = "invalid_request",
                message = "Select a process for this read; a folder identity also requires its store identity."
            };
        }
        JsonObject result = JsonSerializer.SerializeToNode(services.Ui.InspectApplication(query))!.AsObject();
        result["data_trust"] = "untrusted";

        return result;
    }

    private static string? Identity(JsonObject arguments, string name)
    {
        string? value = arguments[name]?.ToString();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private object Operate(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        MailField? field = arguments["field"] is { } value ? Enum.Parse<MailField>(value.ToString()) : null;
        var command = new ApplicationObjectCommand(arguments["pid"]!.GetValue<int>(),
            Enum.Parse<ApplicationOperation>(arguments["operation"]!.ToString()), arguments["entry_id"]?.ToString(),
            arguments["store_id"]?.ToString(), arguments["expected_folder_id"]?.ToString(),
            arguments["destination_folder_id"]?.ToString(), field, arguments["value"]?.DeepClone(),
            arguments["subject"]?.ToString() ?? "", arguments["body"]?.ToString() ?? "",
            Identity(arguments, "expected_content_sha256"), Identity(arguments, "parent_folder_id"),
            arguments["folder_name"]?.ToString() ?? "");

        string? problem = ApplicationCommandValidation.Problem(command);
        if (problem != null)
        {
            return new
            {
                success = false,
                code = "invalid_request",
                message = problem
            };
        }
        ApplicationObjectResult handled = services.Ui.GuardedInput(() => services.RequireApproval(call.Caller), backend =>
        {
            call.MarkBackendMayHaveMutated();
            return backend.OperateApplication(command);
        });
        JsonObject result = JsonSerializer.SerializeToNode(handled)!.AsObject();
        if (handled.Success && handled.Mutation?.Verified == true)
        {
            result["outcome"] = "verified";
        }

        return result;
    }
}
