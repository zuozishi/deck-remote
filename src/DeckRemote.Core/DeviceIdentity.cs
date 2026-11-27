using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DeckRemote.Core;

public sealed record Peer(string Name, string Fingerprint);
public sealed record PairingRequest(string Name, string PcCode, string DeckCode);

public sealed class DeviceIdentity : IDisposable
{
    private readonly string peerFile;
    private Peer? peer;

    public DeviceIdentity(string directory, Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var certificatePath = Path.Combine(directory, "identity.pfx");
        if (!File.Exists(certificatePath))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Deck Remote", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            var encoded = created.Export(X509ContentType.Pkcs12);
            try
            {
                WritePrivate(certificatePath, protect is null ? encoded : protect(encoded));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }

        var stored = File.ReadAllBytes(certificatePath);
        var pfx = unprotect is null ? stored : unprotect(stored);
        try
        {
            // Schannel needs a private key available in the user key store.
            var storage = OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.UserKeySet
                : X509KeyStorageFlags.EphemeralKeySet;
            Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, storage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }

        Fingerprint = GetFingerprint(Certificate);
        peerFile = Path.Combine(directory, "peer.json");
        if (File.Exists(peerFile))
        {
            peer = JsonSerializer.Deserialize<Peer>(File.ReadAllBytes(peerFile));
        }
    }

    internal X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }
    public Peer? Peer => Volatile.Read(ref peer);

    internal static string GetFingerprint(X509Certificate certificate)
    {
        using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    internal static string DisplayCode(string fingerprint)
    {
        return $"${fingerprint[..4]}-${fingerprint[4..8]}-${fingerprint[8..12]}-${fingerprint[12..16]}";
    }

    internal void Trust(Peer value)
    {
        WritePrivate(peerFile, JsonSerializer.SerializeToUtf8Bytes(value));
        Volatile.Write(ref peer, value);
    }

    internal void Forget()
    {
        File.Delete(peerFile);
        Volatile.Write(ref peer, null);
    }

    private static void WritePrivate(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew };
            if (OperatingSystem.IsLinux())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Dispose() => Certificate.Dispose();
}
