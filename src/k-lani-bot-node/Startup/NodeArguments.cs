using System.Globalization;

namespace KLaniBot.Startup;

// The raw command line, read the same way by every command. A value is the token after its option.
// Error messages name the option and what it accepts but never repeat the value: a mistyped token or
// key in the wrong place must not end up in a log that other tools collect.
public sealed class NodeArguments
{
    private readonly string[] _tokens;

    public NodeArguments(string[] tokens)
    {
        _tokens = tokens;
    }

    public IReadOnlyList<string> Tokens => _tokens;

    public bool Has(string name)
    {
        return _tokens.Contains(name);
    }

    public string? Option(string name)
    {
        int index = Array.IndexOf(_tokens, name);
        bool hasValue = index >= 0 && index + 1 < _tokens.Length;
        if (!hasValue)
        {
            return null;
        }
        return _tokens[index + 1];
    }

    // Every value of an option that may be given several times, in order.
    public IReadOnlyList<string> Values(string name)
    {
        var values = new List<string>();
        for (int index = 0; index + 1 < _tokens.Length; index++)
        {
            if (_tokens[index] == name)
            {
                values.Add(_tokens[index + 1]);
            }
        }
        return values;
    }

    // Throws ArgumentException when the option is missing.
    public string RequireOption(string name)
    {
        string? value = Option(name);
        if (value == null)
        {
            throw new ArgumentException(name + " <value> is required.");
        }
        return value;
    }

    // Throws ArgumentException when the value is not a whole number from minimum to maximum.
    public int GetIntInRange(string name, int fallback, int minimum, int maximum)
    {
        int value = OptionalInt(name) ?? fallback;
        if (value < minimum || value > maximum)
        {
            throw new ArgumentException(name + " must be a whole number from " + minimum + " to " + maximum + ".");
        }
        return value;
    }

    // Throws ArgumentException when the value is not a port number.
    public int GetPort(string name, int fallback)
    {
        return GetIntInRange(name, fallback, 1, 65535);
    }

    // Null when the option is absent. Throws ArgumentException when a value is present but not a whole number.
    private int? OptionalInt(string name)
    {
        string? text = Option(name);
        if (text == null)
        {
            return null;
        }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            throw new ArgumentException(name + " must be a whole number.");
        }
        return value;
    }
}
