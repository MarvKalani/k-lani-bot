using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Guard;
using KLaniBot.Startup;
using KLaniBot.Transport;
using KLaniBot.Visibility;

namespace KLaniBot.Fleet;

// Use case: an operator installs many Windows nodes through software distribution instead of pairing each one
// at its screen. The master's "invite" command produces an invitation (node name, consent mode, master address,
// master CA, a one-time token, valid for one hour), and the Windows setup enrolls with it. There is no code to
// compare, so the invitation itself is the credential: whoever holds it can enroll that node name once, and
// it is deleted after use.
//
// A node that is already paired is never moved to another master silently, and an invitation whose node name
// or consent mode differs from the existing local policy is refused instead of overwriting it.
public static class FleetEnrollment
{
    // Throws InvalidDataException for an invalid invitation, settings, master or credential, and UnauthorizedAccessException when an
    // existing pairing or policy does not match the invitation.
    public static async Task<string> EnrollAsync(
        string invitationPath,
        string stateDirectory,
        string installedExecutable,
        CancellationToken cancellation)
    {
        Invitation invitation = ReadInvitation(invitationPath);
        PairingClient.CreatePrivateDirectory(stateDirectory);
        string pairedPath = Path.Combine(stateDirectory, "paired-master.json");
        string configPath = Path.Combine(stateDirectory, "node.json");
        if (File.Exists(pairedPath))
        {
            RequireSameMaster(pairedPath, invitation);
            return configPath;
        }

        await EnsureMatchingPolicyAsync(configPath, stateDirectory, installedExecutable, invitation);
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string credential = await RequestCredentialAsync(invitation, identity, cancellation);
        PairingProtocol.WritePrivate(Path.Combine(stateDirectory, "device-identity.key"), identity.ExportPkcs8PrivateKeyPem(), true);
        PairingProtocol.WritePrivate(pairedPath, new JsonObject
        {
            ["version"] = 1,
            ["master"] = invitation.Master.ToString(),
            ["root"] = invitation.Root,
            ["credential"] = credential,
            ["auto_update"] = true
        }.ToJsonString());
        File.Delete(invitationPath);
        return configPath;
    }

    private const int InvitationFields = 8;
    private const int CredentialBytes = 32;
    private const int MaximumResponseBytes = 16384;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // The checked parts of an invitation file.
    private sealed record Invitation(string Node, ConsentMode ConsentMode, Uri Master, string Root, string Challenge, string Token);

    // Throws InvalidDataException for an unversioned or expired invitation, an invalid node name or consent mode, or a master
    // address that is not a plain HTTPS origin.
    private static Invitation ReadInvitation(string invitationPath)
    {
        CartridgeManifest.RejectLinks(invitationPath);
        JsonObject invitation = JsonNode.Parse(File.ReadAllText(invitationPath))!.AsObject();
        if (invitation.Count != InvitationFields || invitation["version"]?.GetValue<int>() != 1 ||
            DateTimeOffset.Parse(invitation["expires_at"]!.ToString()) <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("Invalid deployment invitation.");
        }
        string node = invitation["node"]!.ToString();
        bool consentModeKnown = ConsentModeNames.TryParse(invitation["consent_mode"]!.ToString(), out ConsentMode consentMode);
        if (!NodeName.IsValid(node) || !consentModeKnown)
        {
            throw new InvalidDataException("Invalid deployment settings.");
        }
        Uri master = new(invitation["master"]!.ToString());
        if (master.Scheme != "https" || master.UserInfo != "" || master.Query != "" || master.Fragment != "" || master.AbsolutePath != "/")
        {
            throw new InvalidDataException("Invalid trusted deployment master.");
        }

        return new Invitation(node, consentMode, master, invitation["root"]!.ToString(), invitation["challenge"]!.ToString(),
            invitation["token"]!.ToString());
    }

    // Throws UnauthorizedAccessException when the existing pairing belongs to another master.
    private static void RequireSameMaster(string pairedPath, Invitation invitation)
    {
        JsonObject existing = JsonNode.Parse(File.ReadAllText(pairedPath))!.AsObject();

        if (existing["master"]!.ToString() != invitation.Master.ToString() || existing["root"]!.ToString() != invitation.Root)
        {
            throw new UnauthorizedAccessException("Existing pairing belongs to another master; do not replace it silently.");
        }
    }

    // Creates the local node on first enrollment; an existing one must already have the invited name and consent mode.
    // Throws UnauthorizedAccessException when the existing local policy differs from the invitation.
    private static async Task EnsureMatchingPolicyAsync(string configPath, string stateDirectory, string installedExecutable,
        Invitation invitation)
    {
        if (!File.Exists(configPath))
        {
            string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "k-lani-bot-work", "desktop");
            await NodeInitializer.InitializeAsync([
                "--workspace", workspace, "--state", stateDirectory, "--node", invitation.Node, "--bind", "127.0.0.1",
                // Same reason as the Windows setup: an invitation carries no guard rules, and guarded without
                // rules reaches no application.
                "--worker-program", installedExecutable, "--scopes", "observe,ui,inventory.read",
                "--ui-guard", "desktop", "--consent-mode", ConsentModeNames.ToProtocolName(invitation.ConsentMode)
            ]);
        }

        NodePolicy policy = NodePolicy.Load(NodeConfiguration.Load(configPath).PolicyPath);
        if (policy.NodeId != invitation.Node || policy.Visibility.ConsentMode != invitation.ConsentMode)
        {
            throw new UnauthorizedAccessException("Invitation does not match the existing local policy.");
        }
    }

    // The signature over the invitation's challenge proves that this enrollment holds the key the master will trust.
    // Throws InvalidDataException when the master returns a credential that is not 32 bytes.
    private static async Task<string> RequestCredentialAsync(Invitation invitation, ECDsa identity, CancellationToken cancellation)
    {
        string publicKey = Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo());
        string proof = PairingProtocol.Hash(ProtocolIdentity.FleetProofDomain, invitation.Node, invitation.Challenge, publicKey);
        string signature = Convert.ToBase64String(identity.SignData(Encoding.UTF8.GetBytes(proof), HashAlgorithmName.SHA256));
        using var client = PinnedClient(invitation);
        using var request = new HttpRequestMessage(HttpMethod.Post, "pair/enroll")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(new JsonObject
            {
                ["node"] = invitation.Node,
                ["public_key"] = publicKey,
                ["signature"] = signature
            }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", invitation.Token);
        request.Headers.Add("X-Request-ID", Guid.CreateVersion7().ToString());

        using var response = await client.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        JsonObject result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!.AsObject();
        string credential = result["credential"]!.ToString();
        if (Convert.FromBase64String(credential).Length != CredentialBytes)
        {
            throw new InvalidDataException("Invalid enrollment credential.");
        }
        return credential;
    }

    // Trusts only the master CA from the invitation; no proxy, cookies or redirects.
    private static HttpClient PinnedClient(Invitation invitation)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = ConnectTimeout
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            PairingProtocol.ValidateCertificate(certificate, errors, invitation.Root);
        return new HttpClient(handler)
        {
            BaseAddress = invitation.Master,
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaximumResponseBytes
        };
    }
}
