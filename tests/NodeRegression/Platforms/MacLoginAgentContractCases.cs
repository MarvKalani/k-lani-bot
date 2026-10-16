using System.Xml.Linq;
using KLaniBot;
using KLaniBot.Platforms.MacOS;

// The macOS cartridge stops or replaces a login agent only when its file is exactly the one the cartridge writes; any
// other content belongs to someone else. An earlier form (KeepAlive without the SuccessfulExit condition) used to be
// accepted as well, a migration path the project no longer keeps.
internal static class MacLoginAgentContractCases
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Mac login agent: " + name);
            }
            passed++;
        }
        XElement Current()
        {
            return MacLoginAgentContract.Create("/owned/node", "/owned/config", "/owned/state").Root!;
        }
        bool Owned(XElement agent)
        {
            return MacLoginAgentContract.MatchesOwned(agent, "/owned/node", "/owned/config", "/owned/state");
        }
        Check(Owned(Current()), "the exact current agent is recognised");
        XElement earlier = Current();
        earlier.Element("dict")!.Elements("key").Single(key => key.Value == "KeepAlive").NextNode!.ReplaceWith(new XElement("true"));
        Check(!Owned(earlier), "an earlier form of the agent is not taken over");
        XElement foreign = Current();
        foreign.Element("dict")!.Elements("string").First().Value = "com.example.other";
        Check(!Owned(foreign), "an agent with another label is not taken over");
        bool otherProgram = MacLoginAgentContract.MatchesOwned(Current(), "/other/node", "/owned/config", "/owned/state");
        Check(!otherProgram, "another program is not taken over");
        return passed;
    }
}
