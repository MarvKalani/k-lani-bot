namespace KLaniBot.Transport;

// What a process of the node executable is: only the worker owns the desktop and its indicator.
public enum ExecutionRole
{
    CommandLine,
    McpController,
    NodeSupervisor,
    NodeWorker,
    NodeEnrollment,
    NodeUpdater,
    LocalServiceCheck
}

// The process decides from its arguments which role it plays, before anything else starts.
public static class ExecutionRoleSelector
{
    // Throws ArgumentException when two roles are requested.
    public static ExecutionRole FromArguments(string[] arguments)
    {
        bool controller = arguments.Contains("--mcp") || arguments.Contains("--pairing-master") || arguments.Contains("--master-admin")
            || arguments.Contains("--controller-tray");
        bool supervisor = arguments.Contains("--serve") || arguments.Contains("--connect");
        bool worker = arguments.Contains("--stdio-worker") || arguments.Contains("--local-http");
        bool enrollment = arguments.Contains("--enroll");
        bool updater = arguments.Contains("--apply-update");
        bool serviceCheck = arguments.Contains("--service-check");
        bool[] roleFlags = [controller, supervisor, worker, enrollment, updater, serviceCheck];
        int selectedRoles = roleFlags.Count(flag => flag);

        if (selectedRoles > 1)
        {
            throw new ArgumentException("Execution roles cannot be combined.");
        }

        if (controller)
        {
            return ExecutionRole.McpController;
        }

        if (supervisor)
        {
            return ExecutionRole.NodeSupervisor;
        }

        if (enrollment)
        {
            return ExecutionRole.NodeEnrollment;
        }
        if (updater)
        {
            return ExecutionRole.NodeUpdater;
        }
        if (serviceCheck)
        {
            return ExecutionRole.LocalServiceCheck;
        }
        return worker ? ExecutionRole.NodeWorker : ExecutionRole.CommandLine;
    }
}
