#if WINDOWS
using System.Globalization;

namespace KLaniBot.Platforms.Windows;

// Inbox sorting creates missing destinations under an observed parent in the same store. Existing names are reused;
// successful creation is verified through the returned native folder, never by a screen position.
internal static class WindowsOutlookFolders
{
    private const int MailFolderType = 6;
    private const int MaximumSiblingFolders = 1000;

    public static ApplicationObjectResult Ensure(WindowsOfficeObjectSession session, ApplicationObjectCommand command)
    {
        object parent = session.Call(session.Namespace, "GetFolderFromID", command.ParentFolderId!, command.StoreId!)!;
        object collection = session.Get(parent, "Folders");
        int count = Convert.ToInt32(session.Get(collection, "Count"), CultureInfo.InvariantCulture);
        if (count > MaximumSiblingFolders)
        {
            return new(false, "Too many sibling folders for a bounded uniqueness check; no folder was created.");
        }
        object? folder = Find(session, collection, command.FolderName, count);
        bool existed = folder != null;
        folder ??= session.Call(collection, "Add", command.FolderName, MailFolderType)!;
        ApplicationFolder identity = WindowsOutlookObjects.ReadFolder(session, folder);
        object actualParent = session.Get(folder, "Parent");
        bool verified = identity.StoreId == command.StoreId
            && string.Equals(identity.Name, command.FolderName, StringComparison.OrdinalIgnoreCase)
            && session.Get(actualParent, "EntryID").ToString() == command.ParentFolderId;

        return new(verified, existed ? "Existing destination folder verified." : "Destination folder created and verified.",
            Folder: identity, Mutation: new(identity.EntryId, identity.StoreId, command.ParentFolderId!, "CreateFolder", verified));
    }

    private static object? Find(WindowsOfficeObjectSession session, object collection, string name, int count)
    {
        for (int index = 1; index <= count; index++)
        {
            object folder = session.Call(collection, "Item", index)!;
            if (string.Equals(session.Get(folder, "Name").ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }
}
#endif
