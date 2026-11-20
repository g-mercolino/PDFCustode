using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace PdfCustode.Server.SharePoint;

public static class GraphAuthentication
{
    public static TokenCredential CreateCredential(SharePointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateAuthentication();
        if (string.Equals(options.Authentication, "ManagedIdentity", StringComparison.OrdinalIgnoreCase))
        {
            var identity = string.IsNullOrEmpty(options.ManagedIdentityClientId)
                ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId);
            return new ManagedIdentityCredential(identity);
        }
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("SharePoint certificate authentication requires the Windows certificate store.");
        var location = string.Equals(options.CertificateStoreLocation, "LocalMachine", StringComparison.OrdinalIgnoreCase) ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        string thumbprint = new(options.CertificateThumbprint.Where(character => !char.IsWhiteSpace(character)).ToArray());
        X509Certificate2? certificate;
        try
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            certificate = certificates.Cast<X509Certificate2>().FirstOrDefault(candidate => candidate.HasPrivateKey
                && candidate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && candidate.NotAfter.ToUniversalTime() > DateTime.UtcNow);
            foreach (var candidate in certificates) if (!ReferenceEquals(candidate, certificate)) candidate.Dispose();
        }
        catch (Exception exception) when (exception is CryptographicException or UnauthorizedAccessException)
        { throw new InvalidOperationException("SharePoint certificate store could not be opened."); }
        if (certificate is null) throw new InvalidOperationException("SharePoint certificate was not found with an accessible private key and current validity.");
        // Azure.Identity retains the selected certificate for the service credential lifetime.
        return new ClientCertificateCredential(options.TenantId, options.ClientId, certificate,
            new ClientCertificateCredentialOptions { AuthorityHost = AzureAuthorityHosts.AzurePublicCloud });
    }

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(60) };
}
