using KLaniBot;

try
{
    if (args.Length != 2)
        throw new ArgumentException("Usage: k-lani-bot-cartridge-review <package-directory> <manifest-schema>");
    Console.WriteLine(
        CartridgePackageReview.Inspect(args[0], args[1]).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex) when (
    ex is ArgumentException or System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException
        or UnauthorizedAccessException)
{
    Console.WriteLine(new System.Text.Json.Nodes.JsonObject
    {
        ["valid_for_quarantine"] = false,
        ["release_approved"] = false,
        ["malware_assessment"] = "not_performed",
        ["error"] = ex.Message
    }.ToJsonString());
    return 2;
}
