#if WINDOWS
using System.Globalization;
using System.Text;

namespace KLaniBot.Platforms.Windows;

// Outlook mail and folder identities replace screen-position selection. Reads are bounded; writes bind the expected
// source folder and verify the returned object. No operation sends a message or executes caller-supplied code.
internal static class WindowsOutlookObjects
{
    private const int OutlookMailClass = 43;
    private const int OutlookMailItem = 0;
    private const int DiscardChanges = 1;

    public static ApplicationObjectResult ReadSelection(WindowsOfficeObjectSession session, int maximumCharacters)
    {
        object? explorer = session.Call(session.Application, "ActiveExplorer");
        if (explorer == null)
        {
            return new(false, "No Outlook Explorer selection is available.");
        }
        object selection = session.Get(explorer, "Selection");
        if (Number(session.Get(selection, "Count")) != 1)
        {
            return new(false, "Select exactly one saved mail before reading it.");
        }
        object item = session.Call(selection, "Item", 1)!;
        RequireMail(session, item);
        ApplicationMail mail = ReadMail(session, item, maximumCharacters);

        return new(true, "Selected mail read through the object model; content is untrusted data.", Mail: mail);
    }

    internal static ApplicationMail ReadMail(WindowsOfficeObjectSession session, object item, int maximumCharacters,
        int bodyOffset = 0, ApplicationMailTextFormat textFormat = ApplicationMailTextFormat.Raw)
    {
        object parent = session.Get(item, "Parent");
        string body = Text(session.Get(item, "Body"));
        int sourceTextBytes = Encoding.UTF8.GetByteCount(body);
        int sourceHtmlBytes = 0;
        string contentHash = ContentHash(session, item, body);
        if (textFormat == ApplicationMailTextFormat.Compact)
        {
            body = ApplicationMailText.Compact(body);
        }
        if (textFormat == ApplicationMailTextFormat.Readable)
        {
            string html = Text(session.Get(item, "HTMLBody"));
            sourceHtmlBytes = Encoding.UTF8.GetByteCount(html);
            body = html.Length == 0 ? ApplicationMailText.Readable(body) : MailHtmlText.Read(html);
        }
        int length = body.Length;
        int start = Math.Min(bodyOffset, length);
        int end = start + Math.Min(maximumCharacters, length - start);
        bool truncated = start > 0 || end < length;
        body = body[start..end];

        return new(Text(session.Get(item, "EntryID")), Text(session.Get(parent, "StoreID")), Text(session.Get(parent, "EntryID")),
            Text(session.Get(item, "Subject")), Text(session.Get(item, "SenderName")), body, truncated,
            start, end < length ? end : null, length, contentHash, textFormat, Text(session.Get(item, "SenderEmailAddress")),
            sourceTextBytes, sourceHtmlBytes);
    }

    public static ApplicationObjectResult ReadFolders(WindowsOfficeObjectSession session, ApplicationObjectQuery query)
    {
        object collection;
        bool stores = query.FolderId == null;
        if (stores)
        {
            collection = session.Get(session.Namespace, "Stores");
        }
        else
        {
            object folder = session.Call(session.Namespace, "GetFolderFromID", query.FolderId!, query.StoreId!)!;
            collection = session.Get(folder, "Folders");
        }
        int total = Number(session.Get(collection, "Count"));
        var folders = new List<ApplicationFolder>();
        for (int index = query.Offset; index < Math.Min(total, query.Offset + query.Limit); index++)
        {
            object value = session.Call(collection, "Item", index + 1)!;
            object folder = stores ? session.Call(value, "GetRootFolder")! : value;
            folders.Add(ReadFolder(session, folder));
        }
        int? next = query.Offset + query.Limit < total ? query.Offset + query.Limit : null;

        return new(true, "One folder-tree page read.", Folders: new(total, next, folders.ToArray()));
    }

    internal static ApplicationFolder ReadFolder(WindowsOfficeObjectSession session, object folder)
    {
        object children = session.Get(folder, "Folders");

        return new(Text(session.Get(folder, "EntryID")), Text(session.Get(folder, "StoreID")),
            Text(session.Get(folder, "Name")), Number(session.Get(children, "Count")) > 0);
    }

    public static ApplicationObjectResult Operate(WindowsOfficeObjectSession session, ApplicationObjectCommand command)
    {
        if (command.Operation == ApplicationOperation.CreateFolder)
        {
            return WindowsOutlookFolders.Ensure(session, command);
        }
        if (command.Operation == ApplicationOperation.CreateDraft)
        {
            return CreateDraft(session, command);
        }
        object item = session.Call(session.Namespace, "GetItemFromID", command.EntryId!, command.StoreId!)!;
        RequireMail(session, item);
        object parent = session.Get(item, "Parent");
        if (Text(session.Get(parent, "EntryID")) != command.ExpectedFolderId
            || Text(session.Get(parent, "StoreID")) != command.StoreId)
        {
            return new(false, "The mail is no longer in the expected source folder; no mutation was sent.");
        }

        return command.Operation switch
        {
            ApplicationOperation.SetField => SetField(session, item, command),
            ApplicationOperation.Save => Save(session, item, command),
            ApplicationOperation.Move => Move(session, item, command),
            ApplicationOperation.Delete => Delete(session, item, command),
            ApplicationOperation.Display => Display(session, item, command),
            ApplicationOperation.Close => Close(session, item, command),
            ApplicationOperation.CreateDraft => throw new System.Diagnostics.UnreachableException(),
            ApplicationOperation.CreateFolder => throw new System.Diagnostics.UnreachableException(),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static ApplicationObjectResult CreateDraft(WindowsOfficeObjectSession session, ApplicationObjectCommand command)
    {
        object item = session.Call(session.Application, "CreateItem", OutlookMailItem)!;
        session.Set(item, "Subject", command.Subject);
        session.Set(item, "Body", command.Body);
        session.Call(item, "Save");

        bool verified = Text(session.Get(item, "Subject")) == command.Subject
            && Text(session.Get(item, "Body")).ReplaceLineEndings("\n") == command.Body.ReplaceLineEndings("\n");

        return Mutation(session, item, command.Operation, verified);
    }

    private static ApplicationObjectResult SetField(WindowsOfficeObjectSession session, object item, ApplicationObjectCommand command)
    {
        MailField field = command.Field!.Value;
        object value = ApplicationCommandValidation.FieldValue(command);
        session.Set(item, field.ToString(), value);
        session.Call(item, "Save");
        object actual = session.Get(item, field.ToString());
        bool verified = string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
            Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal);

        return Mutation(session, item, command.Operation, verified);
    }

    private static ApplicationObjectResult Save(WindowsOfficeObjectSession session, object item, ApplicationObjectCommand command)
    {
        session.Call(item, "Save");

        bool verified = Convert.ToBoolean(session.Get(item, "Saved"), CultureInfo.InvariantCulture)
            && Text(session.Get(item, "EntryID")).Length > 0;

        return Mutation(session, item, command.Operation, verified);
    }

    private static ApplicationObjectResult Move(WindowsOfficeObjectSession session, object item, ApplicationObjectCommand command)
    {
        if (command.ExpectedContentSha256 != null
            && ContentHash(session, item, Text(session.Get(item, "Body"))) != command.ExpectedContentSha256)
        {
            return new(false, "The mail content changed after review; no move was sent. Read and classify it again.");
        }
        object target = session.Call(session.Namespace, "GetFolderFromID", command.DestinationFolderId!, command.StoreId!)!;
        object moved = session.Call(item, "Move", target)!;
        object parent = session.Get(moved, "Parent");
        bool verified = Text(session.Get(parent, "EntryID")) == command.DestinationFolderId;

        return Mutation(session, moved, command.Operation, verified);
    }

    private static string ContentHash(WindowsOfficeObjectSession session, object item, string body)
    {
        object attachments = session.Get(item, "Attachments");
        var content = new System.Text.Json.Nodes.JsonObject
        {
            ["subject"] = Text(session.Get(item, "Subject")),
            ["sender"] = Text(session.Get(item, "SenderName")),
            ["body"] = body,
            ["attachment_count"] = Number(session.Get(attachments, "Count")),
            ["modified"] = Convert.ToDateTime(session.Get(item, "LastModificationTime"), CultureInfo.InvariantCulture)
                .ToString("O", CultureInfo.InvariantCulture)
        };

        return KLaniBot.Common.CanonicalJson.Hash(content);
    }

    private static ApplicationObjectResult Delete(WindowsOfficeObjectSession session, object item, ApplicationObjectCommand command)
    {
        session.Call(item, "Delete");

        return new(true, "Delete dispatched; the source item must be inspected before another operation.",
            Mutation: new(command.EntryId!, command.StoreId!, command.ExpectedFolderId!, "Delete", false));
    }

    private static ApplicationObjectResult Display(WindowsOfficeObjectSession session, object item, ApplicationObjectCommand command)
    {
        session.Call(item, "Display");

        return Mutation(session, item, command.Operation, false);
    }

    private static ApplicationObjectResult Close(WindowsOfficeObjectSession session, object item, ApplicationObjectCommand command)
    {
        session.Call(item, "Close", DiscardChanges);

        return new(true, "Inspector close dispatched; unsaved edits were discarded.",
            Mutation: new(command.EntryId!, command.StoreId!, command.ExpectedFolderId!, "Close", false));
    }

    private static ApplicationObjectResult Mutation(WindowsOfficeObjectSession session, object item, ApplicationOperation operation,
        bool verified)
    {
        object parent = session.Get(item, "Parent");
        var mutation = new ApplicationMutation(Text(session.Get(item, "EntryID")), Text(session.Get(parent, "StoreID")),
            Text(session.Get(parent, "EntryID")), operation.ToString(), verified);

        string message = verified ? "Native object result verified." : "Operation dispatched.";

        return new(verified || operation == ApplicationOperation.Display, message,
            Mutation: mutation);
    }

    // Throws InvalidOperationException when the object is not a saved mail item.
    internal static void RequireMail(WindowsOfficeObjectSession session, object item)
    {
        if (Number(session.Get(item, "Class")) != OutlookMailClass)
        {
            throw new InvalidOperationException("The selected object is not a mail item.");
        }
    }

    private static string Text(object value)
    {
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static int Number(object value)
    {
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
#endif
