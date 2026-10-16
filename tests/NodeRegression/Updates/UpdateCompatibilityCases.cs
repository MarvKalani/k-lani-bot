using System.Text.Json;
using KLaniBot.Transport;
using KLaniBot.Updates;

// Every installed node, of whatever version, finds, verifies and fetches its next update through these names and this
// manifest format. The rename of 2026-10-01 changed them together with their pins and stranded the installed nodes, so this
// test states the reason: a change here needs a planned bridge for every node that is still installed, not a new pin.
internal static class UpdateCompatibilityCases
{
    // A manifest signed with a throwaway key whose private part was never kept; only its certificate is here.
    private const string FixtureRootCertificate =
        "MIIBQjCB6aADAgECAgkAqGzXHsbYWJ0wCgYIKoZIzj0EAwIwJzElMCMGA1UEAxMcdXBkYXRlIGNvbXBhdGliaWxpdHkgZml4"
        + "dHVyZTAeFw0yNjEwMDUwMDAwMDBaFw0zNjEwMDUwMDAwMDBaMCcxJTAjBgNVBAMTHHVwZGF0ZSBjb21wYXRpYmlsaXR5IGZp"
        + "eHR1cmUwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAQ4fUu1Jv7Dr5yXbiBsVBdFvKenm+8AKDNIlyaF00qwF4sjpaTwm1Vw"
        + "5Umlun8XU3E3XQodEDLIvdQ+MxwCG7pBMAoGCCqGSM49BAMCA0gAMEUCIFAg8RLUvIPmfU2sW2NudUW/gsfdTAyXIPYl2JWT"
        + "xuMCAiEAnwyshQc2fYeXrcCaWLa9PCMF/jxiACnWt9hGiYE7bgU=";

    private const string FixtureManifest =
        "{\"Version\":\"0.2610.0501\",\"Platform\":\"windows-x64\",\"Size\":4096,\"Sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaa"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"IssuedAt\":\"2026-10-05T00:00:00+00:00\",\"ExpiresAt\":\"20"
        + "26-11-04T00:00:00+00:00\",\"Signature\":\"1Te6hyXhSstldggvysrFhFqpA/EynQdIC6ZA/pH3HQLYM/GBs/RYxb9pt2"
        + "pMLvPZsHgK6/kRpJ24JeefUoQKTQ==\"}";

    private static readonly DateTimeOffset InsideValidity = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    public static int Run()
    {
        var checks = new Checks("Update compatibility");
        checks.That(ProtocolIdentity.NodeUpdateSignatureDomain == "k-lani-bot/node-update/v1", "the update signature domain is unchanged");
        checks.That(ProtocolIdentity.NodeHeader == "X-K-Lani-Bot-Node" && ProtocolIdentity.VersionHeader == "X-K-Lani-Bot-Version"
            && ProtocolIdentity.UpdatesHeader == "X-K-Lani-Bot-Updates", "the headers a master reads for updates are unchanged");
        checks.That(ProtocolIdentity.UpdateManifestRoute == "updates/windows-x64/manifest"
            && ProtocolIdentity.UpdateFileRoutePrefix == "updates/windows-x64/", "the update routes are unchanged");

        NodeUpdateManifest manifest = JsonSerializer.Deserialize<NodeUpdateManifest>(FixtureManifest)!;
        bool accepted = Accepts(manifest);
        checks.That(accepted, "a manifest signed in the released format still verifies");
        checks.That(manifest.Signature.Length > 0 && manifest.Sha256.Length == 64, "the manifest field names are unchanged");
        return checks.Passed;
    }

    private static bool Accepts(NodeUpdateManifest manifest)
    {
        try
        {
            manifest.Validate(FixtureRootCertificate, "0.2610.0101", InsideValidity);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
