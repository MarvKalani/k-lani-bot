using System.Xml.Linq;
using KLaniBot.Transport;

namespace KLaniBot.Platforms.MacOS;

// The login agent the macOS cartridge writes, and the test whether an existing agent file is that one. The cartridge
// stops or replaces an agent only when the file matches exactly; anything else belongs to someone else and is left
// alone, so a cartridge run can never take over an unrelated service.
public static class MacLoginAgentContract
{
    public static XDocument Create(string executable, string config, string state)
    {
        static IEnumerable<XElement> Pair(string key, string value) => [new XElement("key", key), new XElement("string", value)];
        var programArguments = new XElement(
            "array",
            new[] { executable, "--serve", "--config", config }.Select(s => new XElement("string", s)));
        var documentType = new XDocumentType(
            "plist",
            "-//Apple//DTD PLIST 1.0//EN",
            "http://www.apple.com/DTDs/PropertyList-1.0.dtd",
            null);
        var dictionary = new XElement(
            "dict",
            Pair("Label", ProtocolIdentity.BundleIdentifier),
            new XElement("key", "ProgramArguments"),
            programArguments,
            new XElement("key", "RunAtLoad"),
            new XElement("true"),
            new XElement("key", "KeepAlive"),
            new XElement("dict", new XElement("key", "SuccessfulExit"), new XElement("false")),
            new XElement("key", "ThrottleInterval"),
            new XElement("integer", 30),
            Pair("StandardOutPath", Path.Combine(state, "supervisor.stdout.log")),
            Pair("StandardErrorPath", Path.Combine(state, "supervisor.stderr.log")));
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            documentType,
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
    }
    public static bool MatchesOwned(XElement actual, string executable, string config, string state)
    {
        var expected = Create(executable, config, state);
        return XNode.DeepEquals(actual, expected.Root);
    }
}
