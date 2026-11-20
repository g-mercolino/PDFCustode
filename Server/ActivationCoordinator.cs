using System.Security.Cryptography;
using System.Text;
using PdfCustode.Core;
using PdfCustode.Server.SharePoint;

namespace PdfCustode.Server;

public sealed class ActivationProfileValidationException(string message) : ArgumentException(message);

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
        var profile = ValidateProfile(request);
        int? quota = synchronizer is null ? null : await synchronizer.GetQuotaAsync(request.DocumentId, ct);
        ct.ThrowIfCancellationRequested();
        return catalog.Activate(request, quota, synchronizer?.QuotaYear, profile);
    }

    private static ActivationProfile ValidateProfile(ActivationRequest request)
    {
        string nome = Required(request.Nome, 100, "Nome obbligatorio, massimo 100 caratteri, senza caratteri di controllo.");
        string cognome = Required(request.Cognome, 100, "Cognome obbligatorio, massimo 100 caratteri, senza caratteri di controllo.");
        string codice = Required(request.CodiceUtente, 128, "Codice utente obbligatorio, massimo 128 caratteri, senza caratteri di controllo.");
        string macchina = Required(request.NomeMacchina, 255, "Nome macchina obbligatorio, massimo 255 caratteri, senza caratteri di controllo.");
        string piattaforma = Required(request.Piattaforma, 7, "Piattaforma obbligatoria: Windows o Android.");
        if (piattaforma is not "Windows" and not "Android")
            throw new ActivationProfileValidationException("Piattaforma obbligatoria: Windows o Android.");
        return new(nome, cognome, codice, macchina, piattaforma);
    }

    private static string Required(string? value, int maximum, string message)
    {
        if (value is null || value.Any(char.IsControl)) throw new ActivationProfileValidationException(message);
        string normalized;
        try { normalized = value.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new ActivationProfileValidationException(message); }
        if (normalized.Length == 0 || normalized.Length > maximum) throw new ActivationProfileValidationException(message);
        return normalized;
    }
}
