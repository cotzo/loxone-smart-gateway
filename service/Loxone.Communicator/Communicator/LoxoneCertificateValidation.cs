using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Loxone.Communicator;

internal static class LoxoneCertificateValidation
{
    public static bool Validate(X509Certificate certificate, SslPolicyErrors errors, string rootCertificatePem)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;

        if (string.IsNullOrWhiteSpace(rootCertificatePem))
            return errors == SslPolicyErrors.None;

        try
        {
            using var root = X509Certificate2.CreateFromPem(rootCertificatePem);
            using var leaf = new X509Certificate2(certificate);
            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(root);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return customChain.Build(leaf);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
