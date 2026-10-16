using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KLaniBot.Common;

namespace KLaniBot.Transport;

// Use case: controllers reach a node by IP address on a LAN, where no public certificate authority can vouch
// for it. Each installation therefore has its own CA (node-ca.pem, 10 years) and issues itself short-lived
// leaf certificates for its address. A controller pins that CA by hash in its node registry, so the leaf can
// be rotated without touching any controller, and no other CA on the controller's machine is trusted for it.
//
// The CA key is the node's identity towards its controllers. If only one of its two files is present the node
// refuses to start: generating a new CA would silently break every pin, and the operator should notice that
// state is incomplete instead.
public sealed class ServerCertificates : IDisposable
{
    private readonly string _directory;
    private X509Certificate2 _leaf;
    private readonly List<X509Certificate2> _retired = [];
    public X509Certificate2 Root
    {
        get;
    }
    public X509Certificate2 Current => _leaf;
    // Throws InvalidDataException when only one of certificate and key exists.
    public ServerCertificates(string directory, IPAddress address)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        string certificateFile = Path.Combine(directory, "node-ca.pem"), keyFile = Path.Combine(directory, "node-ca.key");
        if (File.Exists(certificateFile) != File.Exists(keyFile))
            throw new InvalidDataException("Incomplete CA state; restore both files instead of silently replacing trust.");
        if (!File.Exists(certificateFile))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=k-lani-bot installation CA", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(10));
            Private(certificateFile, root.ExportCertificatePem());
            Private(keyFile, key.ExportPkcs8PrivateKeyPem());
        }
        Root = X509Certificate2.CreateFromPemFile(certificateFile, keyFile);
        _leaf = Issue(address);
    }
    private static void Private(string path, string content)
    {
        PrivateFile.WriteAllText(path, content);
    }
    private X509Certificate2 Issue(IPAddress address)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=k-lani-bot node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.Create(
            Root,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(13),
            RandomNumberGenerator.GetBytes(16));
        using var withKey = certificate.CopyWithPrivateKey(key);
        // Windows Schannel cannot serve TLS with a certificate whose private key only lives in memory (event 36869).
        // A PKCS#12 round trip gives the TLS stack a key it can always open; other platforms are unaffected.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }
    public void Rotate(IPAddress address)
    {
        var next = Issue(address);
        _retired.Add(_leaf);
        _leaf = next;
    }
    public void Dispose()
    {
        _leaf.Dispose();
        foreach (var previous in _retired)
            previous.Dispose();
        Root.Dispose();
    }
}
