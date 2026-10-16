using System.Text.Json;
using KLaniBot.CartridgeLab;

if (args.Length == 2 && args[0] == "dynamic-form" && OperatingSystem.IsLinux())
{
    return GtkFixture.RunDynamicForm(Path.GetFullPath(args[1]));
}
if (args.Length == 2 && args[0] == "discovery" && OperatingSystem.IsLinux())
{
    return GtkFixture.RunDiscovery(Path.GetFullPath(args[1]));
}
if (args.Length == 2 && args[0] == "door-form" && OperatingSystem.IsLinux())
{
    return GtkFixture.RunDoorForm(Path.GetFullPath(args[1]));
}
if (args.Length < 1 || args[0] != "fixture")
{
    Console.Error.WriteLine("Dynamic fixture: k-lani-bot-cartridge-lab dynamic-form <result-path>");
    Console.Error.WriteLine("Discovery fixture: k-lani-bot-cartridge-lab discovery <result-path>");
    Console.Error.WriteLine("Door fixture: k-lani-bot-cartridge-lab door-form <result-path>");
    Console.Error.WriteLine("Usage: k-lani-bot-cartridge-lab fixture [observer|model-editor|synthetic-file-manager] [journal-path]");
    return 2;
}
if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine(
    "This native test UI currently implements GTK3/Linux; other platform "
    + "acceptance is pending.");
    return 2;
}
var profile = ActionSafety.Profile(args.Length > 1 ? args[1] : "model-editor");
var model = new FixtureModel(profile, args.Length > 2 ? Path.GetFullPath(args[2]) : null);
return GtkFixture.Run(model, profile.Name);
