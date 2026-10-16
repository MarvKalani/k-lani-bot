using System.Text;

namespace KLaniBot.Platforms.Windows;

// Use case: on Windows the node starts legacy programs through ShellExecute, which only accepts one
// parameter string. The agent sends separate arguments; this joins them so that the target's own
// command-line parser (CommandLineToArgvW / the MSVC runtime) splits them back into exactly the
// same arguments.
//
// Why a hand-written join instead of ProcessStartInfo.ArgumentList: ShellExecute is kept for
// compatibility with programs that need it (for example an application manifest that asks for
// elevation), and its parameter string cannot be built from a list by the framework in a way we
// can verify on every platform. This function is pure, so the quoting rules are tested on Linux.
public static class WindowsCommandLine
{
    public static string Join(IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            AppendArgument(builder, argument);
        }
        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(CharactersThatRequireQuotes) < 0)
        {
            builder.Append(argument);
            return;
        }
        builder.Append('"');
        int pendingBackslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                pendingBackslashes++;
                continue;
            }
            if (character == '"')
            {
                // Backslashes directly before a quote must be doubled, and the quote itself escaped.
                builder.Append('\\', pendingBackslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', pendingBackslashes);
                builder.Append(character);
            }
            pendingBackslashes = 0;
        }
        // Backslashes at the end sit directly before our closing quote and must be doubled.
        builder.Append('\\', pendingBackslashes * 2);
        builder.Append('"');
    }

    private static readonly char[] CharactersThatRequireQuotes = [' ', '\t', '\n', '\v', '"'];
}
