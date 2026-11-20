using System.Security.Cryptography;
using System.Text;
using PdfCustode.Core;
using PdfCustode.Server.SharePoint;

namespace PdfCustode.Server;

public sealed class ActivationCoordinator(ActivationCatalog catalog, SharePointSynchronizer? synchronizer = null)
{
    public async Task<SignedLicense?> ActivateAsync(ActivationRequest request, CancellationToken ct)
    {
        if (request.Code is not { Length: 32 } || !request.Code.All(Uri.IsHexDigit)
            || request.DocumentId is not { Length: > 0 and <= 128 }
            || request.DevicePublicKey is not { Length: > 0 and <= 1024 }) return null;
        try { using var device = Crypto.ReadPublicKey(request.DevicePublicKey); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
        var snapshot = catalog.GetSnapshots().SingleOrDefault(d => d.DocumentId == request.DocumentId);
        string codeHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(request.Code.ToUpperInvariant())));
        var candidate = snapshot?.Codes.SingleOrDefault(c => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(c.CodeHash), Convert.FromHexString(codeHash)));
        if (candidate is null) return null;
        if (candidate.License is not null)
            return candidate.DeviceKeyHash == Crypto.DeviceHash(request.DevicePublicKey) ? candidate.License : null;
        int? quota = synchronizer is null ? null : await synchronizer.GetQuotaAsync(request.DocumentId, ct);
        ct.ThrowIfCancellationRequested();
        return catalog.Activate(request, quota);
    }
}
