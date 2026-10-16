using System.Text.Json;
using System.Text.RegularExpressions;
using KLaniBot.Cartridges;

namespace KLaniBot.Inventory;

// Where to look for one program on one operating system. Every list is optional; an empty list means
// "no hint of this kind". Names are compared the way an installed program is usually listed (see TargetMatcher).
public sealed record TargetDetection(
    string[] DisplayNames,
    string[] Executables,
    string[] Packages,
    string[] DesktopIds,
    string[] AppNames,
    string[] BundleIds)
{
    public static readonly TargetDetection None = new([], [], [], [], [], []);

    public IEnumerable<string> AllLists()
    {
        return DisplayNames.Concat(Executables).Concat(Packages).Concat(DesktopIds).Concat(AppNames).Concat(BundleIds);
    }
}

// The ways a program can be driven other than by operating its windows. Anything on this list is
// normally more reliable than clicking, so it is looked at before a GUI cartridge is written.
public enum ControlRoute
{
    CommandLine,
    Com,
    Uno,
    ScriptingApi,
    AppleScript,
    FileFormat,
    ConfigFile,
    Registry,
    Accessibility
}

// A program the operator wants recognised (from the target catalogue): its names, where to find it on each system and how it can be
// controlled besides its windows.
public sealed record ApplicationTarget(
    string Id,
    string Name,
    string[] Aliases,
    TargetDetection Windows,
    TargetDetection Linux,
    TargetDetection Macos,
    string[] Routes,
    string Notes)
{
    // The names under which a cartridge may refer to this program.
    public IEnumerable<string> KnownNames()
    {
        return Aliases.Prepend(Name);
    }

    public TargetDetection For(string osFamily)
    {
        return osFamily switch
        {
            "windows" => Windows,
            "linux" => Linux,
            "macos" => Macos,
            _ => TargetDetection.None
        };
    }
}

// Use case: "which programs do we want to remote-control, and how do we recognise them on a PC?". The
// target catalogue answers that without listing the whole machine: the scanner looks only for what is
// named here. It also lets an operator say "I have Word and Notepad++" by name.
public static class ApplicationTargetCatalogue
{
    public const string Format = "k-lani-bot.app-targets.v1";

    private static readonly Regex IdPattern = new(
        "^[a-z0-9][a-z0-9-]{0,63}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static readonly IReadOnlyDictionary<string, ControlRoute> RouteNames
        = new Dictionary<string, ControlRoute>(StringComparer.Ordinal)
        {
            ["command_line"] = ControlRoute.CommandLine,
            ["com"] = ControlRoute.Com,
            ["uno"] = ControlRoute.Uno,
            ["scripting_api"] = ControlRoute.ScriptingApi,
            ["applescript"] = ControlRoute.AppleScript,
            ["file_format"] = ControlRoute.FileFormat,
            ["config_file"] = ControlRoute.ConfigFile,
            ["registry"] = ControlRoute.Registry,
            ["accessibility"] = ControlRoute.Accessibility
        };

    // The target catalogue file as written, before it is checked and converted.
    private sealed class FileModel
    {
        public string Format { get; set; } = "";
        public List<TargetModel> Targets { get; set; } = [];
    }

    // One target program in the catalogue file, as written.
    private sealed class TargetModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string[] Aliases { get; set; } = [];
        public DetectionModel Windows { get; set; } = new();
        public DetectionModel Linux { get; set; } = new();
        public DetectionModel Macos { get; set; } = new();
        public string[] Routes { get; set; } = [];
        public string Notes { get; set; } = "";
    }

    // The detection hints for one operating system in the catalogue file, as written.
    private sealed class DetectionModel
    {
        public string[] DisplayNames { get; set; } = [];
        public string[] Executables { get; set; } = [];
        public string[] Packages { get; set; } = [];
        public string[] DesktopIds { get; set; } = [];
        public string[] AppNames { get; set; } = [];
        public string[] BundleIds { get; set; } = [];
    }

    // Throws InvalidDataException when the catalogue is too large, empty, not valid, of unknown format or has duplicate ids.
    public static IReadOnlyList<ApplicationTarget> Load(string file)
    {
        CartridgeManifest.RejectLinks(file);
        if (new FileInfo(file).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("The target catalogue exceeds its size limit.");
        }
        FileModel model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file), Options)
                ?? throw new InvalidDataException("Empty target catalogue.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The target catalogue is not valid: " + exception.Message, exception);
        }
        if (model.Format != Format || model.Targets.Count is < 1 or > 500)
        {
            throw new InvalidDataException("The target catalogue has an unknown format or an unsupported size.");
        }
        var targets = model.Targets.Select(Convert).ToArray();
        if (targets.Select(target => target.Id).Distinct().Count() != targets.Length)
        {
            throw new InvalidDataException("Target ids must be unique.");
        }
        return targets;
    }

    // Throws InvalidDataException for an invalid target or an unknown control route.
    private static ApplicationTarget Convert(TargetModel model)
    {
        if (!IdPattern.IsMatch(model.Id) || model.Name.Length is < 1 or > 128 || model.Notes.Length > 1000)
        {
            throw new InvalidDataException("Invalid target '" + model.Id + "'.");
        }
        var detections = new[] { model.Windows, model.Linux, model.Macos }.Select(ConvertDetection).ToArray();
        RequireBounded(model.Aliases);
        RequireBounded(model.Routes);
        foreach (string route in model.Routes)
        {
            if (!RouteNames.ContainsKey(route))
            {
                throw new InvalidDataException("Unknown control route '" + route + "' for target '" + model.Id + "'.");
            }
        }
        return new ApplicationTarget(
            model.Id,
            model.Name,
            model.Aliases,
            detections[0],
            detections[1],
            detections[2],
            model.Routes,
            model.Notes);
    }

    private static TargetDetection ConvertDetection(DetectionModel model)
    {
        var detection = new TargetDetection(
            model.DisplayNames,
            model.Executables,
            model.Packages,
            model.DesktopIds,
            model.AppNames,
            model.BundleIds);
        RequireBounded(detection.AllLists());
        return detection;
    }

    // Throws InvalidDataException for an empty, too long or control-character hint.
    private static void RequireBounded(IEnumerable<string> values)
    {
        string[] list = values.ToArray();
        if (list.Length > 64 || list.Any(value => value.Length is < 1 or > 128 || value.Any(char.IsControl)))
        {
            throw new InvalidDataException("A target hint is empty, too long or contains control characters.");
        }
    }
}
