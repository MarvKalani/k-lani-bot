using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Platforms;

// Native operations validate identities and field types before the dispatcher marks that the backend may mutate anything.
public static class ApplicationCommandValidation
{
    public static string? Problem(ApplicationObjectCommand command)
    {
        if (command.Operation == ApplicationOperation.CreateFolder)
        {
            return string.IsNullOrWhiteSpace(command.StoreId) || string.IsNullOrWhiteSpace(command.ParentFolderId)
                || string.IsNullOrWhiteSpace(command.FolderName)
                ? "Creating a folder requires its store, observed parent identity and a nonempty name." : null;
        }
        if (command.Operation != ApplicationOperation.CreateDraft
            && (string.IsNullOrWhiteSpace(command.EntryId) || string.IsNullOrWhiteSpace(command.StoreId)
                || string.IsNullOrWhiteSpace(command.ExpectedFolderId)))
        {
            return "A saved mail identity, store and expected source folder are required.";
        }
        if (command.Operation == ApplicationOperation.Move && string.IsNullOrWhiteSpace(command.DestinationFolderId))
        {
            return "Move requires an explicitly identified destination folder.";
        }
        if (command.Operation == ApplicationOperation.SetField)
        {
            return FieldProblem(command);
        }

        return null;
    }

    private static string? FieldProblem(ApplicationObjectCommand command)
    {
        if (command.Field == null || command.Value is not JsonObject value)
        {
            return "SetField requires a supported field and exactly one typed value.";
        }
        string expected = ValueKey(command.Field.Value);
        if (value.Count != 1 || value[expected] == null)
        {
            return "The field value has the wrong native type.";
        }
        int maximum = command.Field == MailField.Importance ? 2 : 3;
        if (expected == "integer" && (value[expected]!.GetValue<int>() < 0 || value[expected]!.GetValue<int>() > maximum))
        {
            return "The enum field value is outside its supported range.";
        }

        return null;
    }

    public static object FieldValue(ApplicationObjectCommand command)
    {
        string key = ValueKey(command.Field!.Value);
        JsonNode value = command.Value![key]!;

        return key == "text" ? value.GetValue<string>()
            : key == "boolean" ? value.GetValue<bool>() : value.GetValue<int>();
    }

    private static string ValueKey(MailField field)
    {
        return field switch
        {
            MailField.Subject or MailField.Body or MailField.HTMLBody or MailField.To or MailField.CC or MailField.BCC => "text",
            MailField.Importance or MailField.Sensitivity => "integer",
            MailField.ReadReceiptRequested or MailField.OriginatorDeliveryReportRequested => "boolean",
            _ => throw new UnreachableException()
        };
    }
}
