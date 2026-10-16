using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Common;

namespace KLaniBot.Startup;

// Use case: node.json, written by --init, tells the supervisor which policy to load, how to start the actuator and where
// to listen. An operator may edit it, so it is read once into this value; a damaged file gives a ReadProblem naming the
// field instead of a null reference somewhere in the start-up.
public sealed record NodeConfiguration(
    string PolicyPath,
    IReadOnlyList<string> WorkerCommand,
    string Bind,
    int HttpsPort,
    int ActionTimeoutMilliseconds)
{
    public const string DefaultBind = "127.0.0.1";
    public const int DefaultHttpsPort = 5443;
    public const int DefaultActionTimeoutMilliseconds = 60000;
    private const int MinimumActionTimeoutMilliseconds = 100;
    private const int MaximumActionTimeoutMilliseconds = 3600000;
    private const int MaximumPort = 65535;

    public TimeSpan ActionTimeout => TimeSpan.FromMilliseconds(ActionTimeoutMilliseconds);

    public static Result<NodeConfiguration, ReadProblem> Read(string path)
    {
        Result<JsonObject, ReadProblem> document = ReadDocument(path);

        return document.Match(FromDocument, problem => problem);
    }

    // Throws InvalidDataException naming the problem. For commands, whose failure is printed and ends the process.
    public static NodeConfiguration Load(string path)
    {
        return Read(path).Match(configuration => configuration, problem => throw new InvalidDataException(problem.Describe()));
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["policy"] = PolicyPath,
            ["worker"] = new JsonArray(WorkerCommand.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
            ["bind"] = Bind,
            ["httpsPort"] = HttpsPort,
            ["actionTimeoutMs"] = ActionTimeoutMilliseconds
        };
    }

    private static Result<JsonObject, ReadProblem> ReadDocument(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject document
                ? document
                : new ReadProblem(ReadProblemKind.NotValid, path + " is not a JSON object.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReadProblem(ReadProblemKind.Unreadable, path + ": " + exception.GetType().Name);
        }
        catch (JsonException)
        {
            return new ReadProblem(ReadProblemKind.NotValid, path + " is not valid JSON.");
        }
    }

    private static Result<NodeConfiguration, ReadProblem> FromDocument(JsonObject document)
    {
        if (TextOf(document, "policy") is not { Length: > 0 } policyPath)
        {
            return new ReadProblem(ReadProblemKind.Missing, "policy");
        }
        if (WorkerCommandOf(document) is not { Count: > 0 } workerCommand)
        {
            return new ReadProblem(ReadProblemKind.Missing, "worker");
        }
        string bind = TextOf(document, "bind") ?? DefaultBind;
        if (!IPAddress.TryParse(bind, out _))
        {
            return new ReadProblem(ReadProblemKind.Refused, "bind is no IP address");
        }
        int? port = NumberOf(document, "httpsPort", DefaultHttpsPort);
        if (port is not (>= 1 and <= MaximumPort))
        {
            return new ReadProblem(ReadProblemKind.Refused, "httpsPort must be between 1 and " + MaximumPort);
        }
        int? timeout = NumberOf(document, "actionTimeoutMs", DefaultActionTimeoutMilliseconds);
        if (timeout is not (>= MinimumActionTimeoutMilliseconds and <= MaximumActionTimeoutMilliseconds))
        {
            return new ReadProblem(ReadProblemKind.Refused, "actionTimeoutMs must be between 100 and 3600000");
        }

        return new NodeConfiguration(policyPath, workerCommand, bind, port.Value, timeout.Value);
    }

    private static string? TextOf(JsonObject document, string name)
    {
        return document[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    // Null when the value is there but no whole number, so the caller refuses it instead of using the default.
    private static int? NumberOf(JsonObject document, string name, int whenAbsent)
    {
        if (document[name] is null)
        {
            return whenAbsent;
        }

        return document[name] is JsonValue value && value.TryGetValue(out int number) ? number : null;
    }

    private static IReadOnlyList<string>? WorkerCommandOf(JsonObject document)
    {
        if (document["worker"] is not JsonArray parts)
        {
            return null;
        }

        var command = new List<string>();
        foreach (JsonNode? part in parts)
        {
            if (part is not JsonValue value || !value.TryGetValue(out string? argument))
            {
                return null;
            }
            command.Add(argument);
        }
        return command;
    }
}
