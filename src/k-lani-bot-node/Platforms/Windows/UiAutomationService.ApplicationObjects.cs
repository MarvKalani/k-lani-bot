#if WINDOWS
using System.Globalization;
using System.Diagnostics;
using KLaniBot.Startup;

namespace KLaniBot.Platforms.Windows;

public partial class UiAutomationService
{
    public ApplicationObjectResult InspectApplication(ApplicationObjectQuery query)
    {
        if (query.Area == ApplicationReadArea.Status)
        {
            return ApplicationObjectThread.Run(ReadOutlookProgramStatus);
        }
        WindowInfo? window = ListWindows().FirstOrDefault(item => item.ProcessId == query.ProcessId);
        if (window == null || !string.Equals(window.ProcessName, "OUTLOOK", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "The selected process is not a running Outlook Classic instance.");
        }

        return ApplicationObjectThread.Run(() => InspectOffice(query, window));
    }

    private static ApplicationObjectResult InspectOffice(ApplicationObjectQuery query, WindowInfo window)
    {
        try
        {
            using var session = new WindowsOfficeObjectSession(query.ProcessId);

            return query.Area switch
            {
                ApplicationReadArea.Info => new(true, "Native application identity read.", Info: ReadOfficeInfo(window, session)),
                ApplicationReadArea.Api => new(true, "Installed object library inspected; members are discovered, not execution-tested.",
                    Api: WindowsOfficeApiCatalog.Read(session.Application, query.TypeName, query.Offset, query.Limit)),
                ApplicationReadArea.Selection => WindowsOutlookObjects.ReadSelection(session, query.MaximumCharacters),
                ApplicationReadArea.Folders => WindowsOutlookObjects.ReadFolders(session, query),
                ApplicationReadArea.Inbox => WindowsOutlookMailReader.ReadInbox(session, query),
                ApplicationReadArea.MailPage => WindowsOutlookMailReader.ReadPage(session, query),
                ApplicationReadArea.Mail => WindowsOutlookMailReader.ReadItem(session, query),
                ApplicationReadArea.Status => throw new UnreachableException(),
                _ => throw new System.Diagnostics.UnreachableException()
            };
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("Application inspection: " + Diagnostics.Describe(failure));
            return new(false, "The native application query was refused or its object became unavailable: " + FailureCode(failure));
        }
    }

    private static ApplicationObjectResult ReadOutlookProgramStatus()
    {
        int sessionId = WindowsDesktopSession.Read().SessionId;
        var identities = new List<int>();
        foreach (Process process in Process.GetProcessesByName("OUTLOOK"))
        {
            using (process)
            {
                if (process.SessionId == sessionId)
                {
                    identities.Add(process.Id);
                }
            }
        }

        return new(true, "Outlook processes checked in the operated Windows session.",
            Status: new(identities.Count > 0, identities.ToArray()));
    }

    private static ApplicationInformation ReadOfficeInfo(WindowInfo window, WindowsOfficeObjectSession session)
    {
        string language = "unknown";
        try
        {
            const int UserInterfaceLanguage = 2;
            object settings = session.Get(session.Application, "LanguageSettings");
            int locale = Convert.ToInt32(session.Get(settings, "LanguageID", UserInterfaceLanguage), CultureInfo.InvariantCulture);
            language = CultureInfo.GetCultureInfo(locale).Name;
        }
        catch (Exception)
        {
            // A missing language property does not make the separately observed program identity unknown.
        }

        return new(window.ProcessId, "OutlookClassic", session.Get(session.Application, "Version").ToString()!,
            window.AppVersion, language, true, ["COM", "UIA", "keyboard"]);
    }

    public ApplicationObjectResult OperateApplication(ApplicationObjectCommand command)
    {
        return ApplicationObjectThread.Run(() => OperateOffice(command));
    }

    private static ApplicationObjectResult OperateOffice(ApplicationObjectCommand command)
    {
        try
        {
            WindowsDesktopSession.Read().RequireInputAvailable();
            using var session = new WindowsOfficeObjectSession(command.ProcessId);

            return WindowsOutlookObjects.Operate(session, command);
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("Application operation: " + Diagnostics.Describe(failure));
            return new(false, "Native operation failed; inspect the item before another mutation: " + FailureCode(failure));
        }
    }

    private static string FailureCode(Exception failure)
    {
        Exception cause = failure.InnerException ?? failure;

        return cause.GetType().Name + " (0x" + cause.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")";
    }
}
#endif
