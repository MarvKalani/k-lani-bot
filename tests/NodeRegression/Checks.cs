// Counts the passed checks of one suite. The first failing check ends the run and names the suite and the check.
internal sealed class Checks(string suite)
{
    public int Passed
    {
        get;
        private set;
    }

    public void That(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception(suite + ": " + name);
        }
        Passed++;
    }

    // For a check that passes by reaching a line, such as an expected exception being caught.
    public void Reached()
    {
        Passed++;
    }
}
