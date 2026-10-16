using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Automatic capture names stay stable under caption changes with an Automation ID, while a scope hash distinguishes semantic identities.
public static class CompactAutomaticNames
{
    private const int MaximumCaptionLength = 42;
    private const int HashCharacters = 8;
    private const string GeneratedPrefix = "ui.";
    private static readonly Regex CombiningMarks = new(@"[\u0300-\u036f]", RegexOptions.CultureInvariant);
    private static readonly Regex NonCaption = new("[^a-z0-9]+", RegexOptions.CultureInvariant);

    // Every automatic name starts with this prefix, so listings can tell a generated function from a named one without parsing its hash.
    public static bool IsGenerated(string name)
    {
        return name.StartsWith(GeneratedPrefix, StringComparison.Ordinal);
    }

    public static bool TryName(CompactSemanticTarget target, CompactRecipeAction action, out string? name)
    {
        name = null;
        try
        {
            string raw = target.AutoId.Length > 0 ? target.AutoId : target.Name;
            string caption = CombiningMarks.Replace(raw.Normalize(NormalizationForm.FormKD), "").ToLowerInvariant();
            caption = NonCaption.Replace(caption, "_").Trim('_');
            caption = caption[..Math.Min(caption.Length, MaximumCaptionLength)];
            if (caption.Length == 0)
            {
                caption = "element";
            }
            var identity = new JsonObject { ["role"] = target.Role.Name };
            if (target.AutoId.Length > 0)
            {
                identity["autoId"] = target.AutoId;
            }
            else
            {
                if (target.WindowKind is not CompactWindowKind kind)
                {
                    return false;
                }
                identity["name"] = target.Name;
                identity["window"] = CompactWindowKindWords.Text(kind);
            }
            identity["containerPath"] = CompactDiscoveryWire.ContainerPath(target.ContainerPath ?? []);
            string digest = CompactFormFields.Fingerprint(identity)[..HashCharacters];
            name = GeneratedPrefix + caption + "." + CompactCaptureNames.Action(action) + "." + digest;

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
