using KLaniBot.Common;
using KLaniBot.Platforms.Linux;

// Package ownership fixtures are explicitly synthetic; the separate installed-system probe never invents application versions.
internal static class LinuxExecutableVersionCases
{
    public static void Run()
    {
        var commands = new List<IReadOnlyList<string>>();
        var reader = new LinuxExecutableVersion(arguments =>
        {
            commands.Add(arguments);
            return Ok(arguments[0] == "-S" ? "image-editor:amd64: /usr/bin/image-editor\n"
                : "image-editor:amd64\tii \t1:2.3.4-5ubuntu1\n");
        }, path => path);
        Require(reader.TryRead("/usr/bin/image-editor", out var build) && build!.Package == "image-editor:amd64"
            && build.Version == "1:2.3.4-5ubuntu1", "architecture and exact installed build are preserved");
        Require(commands[0].SequenceEqual(new[] { "-S", "--", "/usr/bin/image-editor" })
            && commands[1].Last() == "image-editor:amd64", "only fixed system query arguments, no app launch or name-based version");
        UnknownOwnership();
        UnknownVersion();
        ResolutionAndFailures();
        InterpreterScripts();
        MozillaApplicationIni();
    }

    private static void UnknownOwnership()
    {
        foreach (string ownership in new[] { "", "other: /usr/bin/other\n", "a, b: /usr/bin/editor\n",
            "a: /usr/bin/editor\nb: /usr/bin/editor\n", "malformed", "a: /usr/bin/editor*\n", "a;evil: /usr/bin/editor\n" })
        {
            int calls = 0;
            var reader = new LinuxExecutableVersion(_ =>
            {
                calls++;
                return Ok(ownership);
            }, path => path);
            Require(!reader.TryRead("/usr/bin/editor", out _) && calls == 1,
                "absent/ambiguous/malformed/glob owner cannot become package evidence");
        }
    }

    private static void UnknownVersion()
    {
        foreach (string version in new[] { "", "editor\trc \t1.0\n", "editor\tii \t\n", "other\tii \t1.0\n",
            "editor\tii \t1.0\neditor\tii \t2.0\n", "editor\tii \tunknown\n", "editor\tii \t1.0 extra\n",
            "editor\tii \t" + new string('1', 129) + "\n" })
        {
            var reader = new LinuxExecutableVersion(arguments => Ok(arguments[0] == "-S" ? "editor: /usr/bin/editor\n" : version),
                path => path);
            Require(!reader.TryRead("/usr/bin/editor", out _), "only one installed exact bounded version is accepted");
        }
    }

    private static void ResolutionAndFailures()
    {
        int calls = 0;
        var missing = new LinuxExecutableVersion(_ =>
        {
            calls++;
            return Ok("");
        }, _ => null);
        Require(!missing.TryRead("/private/copy/mousepad", out _) && calls == 0,
            "private copy cannot borrow a version from its process name");
        Require(!missing.TryRead(null, out _) && !missing.TryRead("relative", out _), "unknown/relative executable is not queried");
        var failed = new LinuxExecutableVersion(_ => new(false, "editor: /usr/bin/editor\n", "failed", 1), path => path);
        Require(!failed.TryRead("/usr/bin/editor", out _), "failed query output is not trusted");
        var throwing = new LinuxExecutableVersion(_ => throw new IOException("synthetic query failure"), path => path);
        Require(!throwing.TryRead("/usr/bin/editor", out _), "ordinary query errors remain unknown without throwing");
    }

    private static void InterpreterScripts()
    {
        var commands = new List<IReadOnlyList<string>>();
        var reader = new LinuxExecutableVersion(arguments =>
        {
            commands.Add(arguments);
            return Ok(arguments[0] == "-S" ? "tryton-client: /usr/bin/tryton\n" : "tryton-client\tii \t6.0.34-1\n");
        }, path => path);
        var commandLine = new[] { "/usr/bin/python3", "/usr/bin/tryton", "-u", "admin" };
        Require(reader.TryRead("/usr/bin/python3.12", commandLine, out var build) && build!.Package == "tryton-client"
            && build.Version == "6.0.34-1", "an interpreter process reports the package of its script");
        Require(commands.All(arguments => !arguments.Contains("/usr/bin/python3.12")), "the interpreter package is never queried");
        var unowned = new LinuxExecutableVersion(arguments => Ok(arguments[0] == "-S" ? "" : "python3.12\tii \t3.12.3-1\n"), path => path);
        Require(!unowned.TryRead("/usr/bin/python3.12", commandLine, out _), "an unowned script gets no interpreter version");
        var optionsOnly = new[] { "/usr/bin/python3", "-c", "print(1)" };
        Require(!reader.TryRead("/usr/bin/python3.12", optionsOnly, out _), "an interpreter without a script path has no version");
        Require(!reader.TryRead("/usr/bin/python3.12", new[] { "/usr/bin/python3", "tryton" }, out _),
            "a relative script name is not queried");
    }

    private static void MozillaApplicationIni()
    {
        const string thunderbird = "[App]\nVendor=Mozilla\nName=Thunderbird\nVersion=140.17.0\nBuildID=20260929234105\n";
        var asked = new List<string>();
        var reader = new LinuxExecutableVersion(_ => Ok(""), path => path, path =>
        {
            asked.Add(path);
            return thunderbird;
        });
        Require(reader.TryRead("/opt/thunderbird/thunderbird-bin", out var build) && build!.Package == "Thunderbird"
            && build.Version == "140.17.0", "application.ini next to the executable names product and version");
        Require(asked.SequenceEqual(new[] { "/opt/thunderbird/application.ini" }), "only the neighbouring application.ini is read");
        foreach (string text in new[] { "[App]\nName=Thunderbird\n", "[App]\nName=Thunderbird\nVersion=unknown\n",
            "[Gecko]\nVersion=140.17.0\n[App]\nName=Thunderbird\n", "[App]\nName=Thunder;bird\nVersion=140.17.0\n" })
        {
            var rejecting = new LinuxExecutableVersion(_ => Ok(""), path => path, _ => text);
            Require(!rejecting.TryRead("/opt/thunderbird/thunderbird-bin", out _), "incomplete or unsafe application.ini is refused");
        }
    }

    private static ProcessResult Ok(string output)
    {
        return new(true, output, "", 0);
    }

    private static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}
