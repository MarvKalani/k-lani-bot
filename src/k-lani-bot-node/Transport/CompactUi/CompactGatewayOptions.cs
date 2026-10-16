using KLaniBot.Startup;

namespace KLaniBot.Transport.CompactUi;

// MCP startup selects a finite profile before it opens clients or private application state.
public enum CompactGatewayProfile
{
    Core,
    Compact
}

// Validated compact startup keeps public library and private learning roots distinct; absent learning never grants persistence.
public sealed record CompactGatewayOptions(CompactGatewayProfile Profile, CompactStorageRoots? Roots,
    CompactGatewayConfiguration? Configuration = null);

// Profile parsing preserves the original no-profile MCP invocation and rejects conflicting or incomplete compact options before startup.
public static class CompactGatewayOptionCodec
{
    // Throws ArgumentException for invalid profile/options/roots or options used with a different command.
    public static CompactGatewayOptions Read(NodeArguments arguments, NodeCommand command)
    {
        if (!arguments.Has("--ui-profile") && !arguments.Has("--learning-root") && !arguments.Has("--ui-config"))
        {
            return new(CompactGatewayProfile.Core, null);
        }
        if (command != NodeCommand.Mcp)
        {
            throw new ArgumentException("UI profiles, learning roots and UI configuration require --mcp.");
        }
        RequireSingleValue(arguments, "--ui-profile");
        if (arguments.Option("--ui-profile") != "compact")
        {
            throw new ArgumentException("Use --ui-profile compact or omit the profile for the existing core gateway.");
        }
        foreach (string option in new[] { "--cartridges", "--nodes" })
        {
            RequireSingleValue(arguments, option);
        }
        foreach (string option in new[] { "--learning-root", "--default-node", "--ui-config" })
        {
            if (arguments.Has(option))
            {
                RequireSingleValue(arguments, option);
            }
        }
        if (arguments.Has("--mcp-full-text") || arguments.Has("--advisor-config"))
        {
            throw new ArgumentException("Compact UI profile cannot be combined with full-text or advisor options.");
        }

        return Roots(arguments);
    }

    // Throws ArgumentException for a missing, duplicate or option-shaped value, without printing potentially private input.
    private static void RequireSingleValue(NodeArguments arguments, string option)
    {
        string? value = arguments.Option(option);
        if (arguments.Tokens.Count(token => token == option) != 1 || string.IsNullOrWhiteSpace(value)
            || value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(option + " requires exactly one value.");
        }
    }

    // Throws ArgumentException for relative, overlapping, linked or unavailable library/private roots.
    private static CompactGatewayOptions Roots(NodeArguments arguments)
    {
        if (!CompactProgramStorage.TryRoots(arguments.RequireOption("--cartridges"), arguments.Option("--learning-root"), out var roots)
            || !Directory.Exists(roots.Value!.Library))
        {
            throw new ArgumentException("Use an existing absolute cartridge library and a separate safe absolute learning root.");
        }

        return new(CompactGatewayProfile.Compact, roots.Value,
            CompactGatewayConfigurationCodec.Read(arguments.Option("--ui-config"), roots.Value));
    }
}
