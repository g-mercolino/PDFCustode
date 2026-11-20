namespace PdfCustode.Server.SharePoint;
public sealed class SharePointOptions
{
    public bool Enabled { get; set; }
    public string SiteId { get; set; } = "";
    public string SiteHostname { get; set; } = "innowatedev.sharepoint.com";
    public string DistributionListId { get; set; } = "";
    public string LicenseListId { get; set; } = "";
    public string LogListId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Authentication { get; set; } = "Certificate";
    public string CertificateThumbprint { get; set; } = "";
    public string CertificateStoreLocation { get; set; } = "CurrentUser";
    public string ManagedIdentityClientId { get; set; } = "";
    public int SyncIntervalSeconds { get; set; } = 30;
    public int QuotaYear { get; set; }
    public int InitialLicenseTotal { get; set; }
    public List<string> DocumentIds { get; set; } = [];
    public void Validate()
    {
        if (!Enabled) return;
        string[] site = (SiteId ?? "").Split(',');
        if (string.IsNullOrWhiteSpace(SiteHostname) || Uri.CheckHostName(SiteHostname) != UriHostNameType.Dns
            || !SiteHostname.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
            || site.Length != 3 || !string.Equals(site[0], SiteHostname, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(site[1], out _) || !Guid.TryParse(site[2], out _))
            throw Invalid("SiteId/SiteHostname");
        string[] lists = [DistributionListId, LicenseListId, LogListId];
        if (lists.Any(id => !Guid.TryParse(id, out _)) || lists.Select(Guid.Parse).Distinct().Count() != 3)
            throw Invalid("list IDs");
        if (SyncIntervalSeconds < 1 || SyncIntervalSeconds > 86400) throw Invalid("SyncIntervalSeconds");
        if (QuotaYear is < 1 or > 9999) throw Invalid("QuotaYear");
        if (InitialLicenseTotal < 0) throw Invalid("InitialLicenseTotal");
        if (DocumentIds is null || DocumentIds.Count == 0
            || DocumentIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
            || DocumentIds.Distinct(StringComparer.Ordinal).Count() != DocumentIds.Count)
            throw Invalid("DocumentIds");
        ValidateAuthentication();
    }

    internal void ValidateAuthentication()
    {
        if (string.Equals(Authentication, "Certificate", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(TenantId, out _) || !Guid.TryParse(ClientId, out _) || string.IsNullOrWhiteSpace(CertificateThumbprint))
                throw Invalid("certificate identity");
            if (!string.Equals(CertificateStoreLocation, "CurrentUser", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(CertificateStoreLocation, "LocalMachine", StringComparison.OrdinalIgnoreCase))
                throw Invalid("CertificateStoreLocation");
            string thumbprint = new(CertificateThumbprint.Where(character => !char.IsWhiteSpace(character)).ToArray());
            if (thumbprint.Length != 40 || thumbprint.Any(character => !Uri.IsHexDigit(character))) throw Invalid("CertificateThumbprint");
        }
        else if (string.Equals(Authentication, "ManagedIdentity", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(ManagedIdentityClientId) && !Guid.TryParse(ManagedIdentityClientId, out _))
                throw Invalid("ManagedIdentityClientId");
        }
        else throw Invalid("Authentication (Certificate or ManagedIdentity)");
    }

    static InvalidOperationException Invalid(string setting) => new($"SharePoint configuration is invalid: {setting}.");
}
