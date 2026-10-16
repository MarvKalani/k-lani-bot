using KLaniBot;
using KLaniBot.Startup;

// Every setup path suggests the computer's name as the node name, so the conversion must give a name pairing
// accepts for the names computers really have, and admit when nothing usable is left.
internal static class NodeNameCases
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Node name: " + name);
            }
            passed++;
        }
        Check(NodeName.SuggestFrom("DESKTOP-4F2K9QH") == "desktop-4f2k9qh", "a Windows computer name is lowercased");
        Check(NodeName.SuggestFrom("Studio-Mac-mini.local") == "studio-mac-mini", "the domain part is dropped");
        Check(NodeName.SuggestFrom("Studio Mac mini") == "studio-mac-mini", "spaces become hyphens");
        Check(NodeName.SuggestFrom("Müller-PC") == "muller-pc", "accents are dropped, not replaced");
        Check(NodeName.SuggestFrom("--build__box--") == "build__box", "the name starts and ends with a letter or digit");
        Check(NodeName.SuggestFrom("a  --  b") == "a-b", "runs of separators collapse");
        string longName = new string('x', 60);
        Check(NodeName.SuggestFrom(longName) == new string('x', 48), "a long name is cut to the limit");
        Check(NodeName.SuggestFrom("東京") == null, "a name without usable characters gives no suggestion");
        Check(NodeName.SuggestFrom("") == null, "an empty name gives no suggestion");
        Check(NodeName.SuggestForThisComputer() is not { } own || NodeName.IsValid(own), "this computer's suggestion is valid");
        Check(NodeName.IsValid("node_1") && !NodeName.IsValid("-node") && !NodeName.IsValid(""), "the pairing pattern is enforced");
        bool refused = false;
        try
        {
            NodeName.Require("bad name");
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        Check(refused, "an invalid chosen name is refused with a reason");
        return passed;
    }
}
