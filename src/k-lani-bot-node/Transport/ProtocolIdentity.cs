
namespace KLaniBot.Transport;

// The names that nodes, masters and the operating system use to recognize each other. They are hashed,
// signed, sent on the wire or registered with macOS, so they live in one place and a test pins every value.
// An installed node of any earlier version still uses them to find, verify and fetch its next update, so a change
// to the update headers, routes or signature strands every installed node that has not been updated yet.
public static class ProtocolIdentity
{
    // HTTP headers between controller and node.
    public const string SessionHeader = "X-K-Lani-Bot-Session";
    public const string PresentationHeader = "X-K-Lani-Bot-Presentation";
    public const string NodeHeader = "X-K-Lani-Bot-Node";
    public const string VersionHeader = "X-K-Lani-Bot-Version";
    public const string UpdatesHeader = "X-K-Lani-Bot-Updates";
    // A node that understands the master's backup requirement message says so; older nodes would end the connection on it.
    public const string BackupRequirementHeader = "X-K-Lani-Bot-Backup-Requirement";
    // A node that answers the master's vitals request says so; the master never sends it to a node that did not.
    public const string VitalsHeader = "X-K-Lani-Bot-Vitals";
    // One value per controller process, shared by its sessions to all nodes, so the master shows and pauses one MCP client
    // as one row however many nodes it uses.
    public const string InstanceHeader = "X-K-Lani-Bot-Instance";

    // Prefixes that are hashed or signed together with the content, so they separate one use from another.
    public const string PairingCommitDomain = "k-lani-bot/pairing/v1/commit";
    public const string PairingSessionDomain = "k-lani-bot/pairing/v1/session";
    public const string FleetProofDomain = "k-lani-bot/fleet/v1";
    public const string NodeUpdateSignatureDomain = "k-lani-bot/node-update/v1";

    // Where a node fetches the signed update manifest and, below the prefix, the executable of a version.
    public const string UpdateManifestRoute = "updates/windows-x64/manifest";
    public const string UpdateFileRoutePrefix = "updates/windows-x64/";
    public const string UpdateStatusRoute = "updates/windows-x64/status";

    // What a node asks on the local network to find its master.
    public const string MasterDiscoveryQuery = "k-lani-bot/master/v1";

    // Name of the local administration channel of the master; the rest of the name is derived from the state directory.
    public const string MasterAdministrationChannelPrefix = "k-lani-bot-master-";

    // Registered with macOS (login service, privacy approvals). The same value is in the Apple cartridge,
    // the test bundle and the bundle script; a test compares them.
    public const string BundleIdentifier = "com.k-lani.bot.node";
}
