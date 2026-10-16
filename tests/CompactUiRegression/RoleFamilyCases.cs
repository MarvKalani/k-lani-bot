using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Platform role names must classify the same in the native gateway and the JS desktop module; the shared fixture states the contract.
internal static class RoleFamilyCases
{
    private const string FixturePath = "tests/CompactUiRegression/fixtures/role-families.json";

    public static void Run()
    {
        string repository = SharedParityCases.FindRepository();
        string file = Path.Combine(repository, FixturePath);
        JsonArray observed = Observe(file);
        RequireExpected(file, observed);
        SharedParityCases.RequireSame(repository, file, observed);
    }

    // Each row reports the native family, discovery field kind, form field kind and plain-text-entry rule of one role name.
    public static JsonArray Observe(string file)
    {
        JsonArray cases = JsonNode.Parse(File.ReadAllText(file))!.AsObject()["cases"]!.AsArray();
        var observed = new JsonArray();
        foreach (JsonNode? entry in cases)
        {
            observed.Add(ObserveCase(entry!.AsObject()));
        }

        return observed;
    }

    private static JsonObject ObserveCase(JsonObject entry)
    {
        CompactRole role = CompactRole.Parse(entry["role"]!.GetValue<string>());

        return new()
        {
            ["platform"] = entry["platform"]!.DeepClone(),
            ["role"] = entry["role"]!.DeepClone(),
            ["family"] = role.Family.ToString(),
            ["fieldKind"] = FieldKindName(role),
            ["formKind"] = FormKindName(role),
            ["plainTextEntry"] = JsonValue.Create(CompactRoleVocabulary.IsPlainTextEntry(role.Name))
        };
    }

    private static string? FieldKindName(CompactRole role)
    {
        return CompactDiscoveryFields.TryKind(role, out CompactFormFieldKind kind) ? kind.ToString().ToLowerInvariant() : null;
    }

    // Form fields are only text and choice roles, so every other role name reports none.
    private static string FormKindName(CompactRole role)
    {
        return CompactFormFields.TryKind(role, out CompactFormFieldKind kind) ? kind.ToString().ToLowerInvariant() : "none";
    }

    // The fixture is the contract; every differing row names the role and both the observed and the expected values.
    private static void RequireExpected(string file, JsonArray observed)
    {
        JsonArray expected = JsonNode.Parse(File.ReadAllText(file))!.AsObject()["cases"]!.AsArray();
        var differences = new List<string>();
        for (int index = 0; index < expected.Count; index++)
        {
            if (!JsonNode.DeepEquals(observed[index], expected[index]))
            {
                differences.Add(observed[index]!.ToJsonString() + " expected " + expected[index]!.ToJsonString());
            }
        }
        if (differences.Count > 0)
        {
            throw new InvalidOperationException("Role family differs from the fixture: " + string.Join("; ", differences));
        }
    }
}
