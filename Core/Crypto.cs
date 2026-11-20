using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace PdfCustode.Core;

public sealed record EncryptedDocument(string DocumentId, string Title, string Nonce, string Ciphertext, string Tag);
public sealed record ActivationRequest(string Code, string DocumentId, string DevicePublicKey,
    string? Nome = null, string? Cognome = null, string? CodiceUtente = null,
    string? NomeMacchina = null, string? Piattaforma = null);
public sealed record ActivationProfile(string Nome, string Cognome, string CodiceUtente, string NomeMacchina, string Piattaforma);
public sealed record LicensePayload(string LicenseId, string DocumentId, string DeviceKeyHash, string WrappedKey, string IssuedAt, string? ExpiresAt = null);
public sealed record SignedLicense(string Payload, string Signature);

public sealed class LicenseExpiredException() : CryptographicException("La licenza di 30 giorni è scaduta. Richiedi un nuovo codice di attivazione.");

public static class Crypto
{
    public static EncryptedDocument Encrypt(byte[] pdf, string id, string title, byte[] key)
    {
        using var aes = CreateCipher(key);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] ciphertext = new byte[pdf.Length];
        byte[] tag = new byte[16];
        aes.Encrypt(nonce, pdf, ciphertext, tag, Encoding.UTF8.GetBytes(id));
        return new(id, title, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    public static byte[] Decrypt(EncryptedDocument document, byte[] key)
    {
        using var aes = CreateCipher(key);
        byte[] ciphertext = Convert.FromBase64String(document.Ciphertext);
        byte[] pdf = new byte[ciphertext.Length];
        aes.Decrypt(Convert.FromBase64String(document.Nonce), ciphertext,
            Convert.FromBase64String(document.Tag), pdf, Encoding.UTF8.GetBytes(document.DocumentId));
        return pdf;
    }

    public static SignedLicense Sign(LicensePayload license, RSA authority)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(license);
        byte[] signature = authority.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new(Convert.ToBase64String(payload), Convert.ToBase64String(signature));
    }

    public static LicensePayload Verify(SignedLicense license, string publicKey, string documentId, string devicePublicKey, DateTimeOffset? now = null)
    {
        using var authority = ReadPublicKey(publicKey);
        byte[] payload = Convert.FromBase64String(license.Payload);
        if (!authority.VerifyData(payload, Convert.FromBase64String(license.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Firma della licenza non valida.");
        var decoded = JsonSerializer.Deserialize<LicensePayload>(payload);
        if (decoded is null || decoded.DocumentId != documentId || decoded.DeviceKeyHash != DeviceHash(devicePublicKey))
            throw new CryptographicException("La licenza non corrisponde al documento o al dispositivo.");
        var expiration = GetExpiration(decoded);
        var current = now ?? DateTimeOffset.UtcNow;
        if (current >= expiration) throw new LicenseExpiredException();
        if (DateTimeOffset.ParseExact(decoded.IssuedAt, "O", CultureInfo.InvariantCulture) - current > TimeSpan.FromMinutes(5))
            throw new CryptographicException("Controlla la data e l’ora del dispositivo.");
        return decoded;
    }

    public static DateTimeOffset GetExpiration(LicensePayload license)
    {
        if (!DateTimeOffset.TryParseExact(license.IssuedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued)
            || issued > DateTimeOffset.MaxValue.AddDays(-30))
            throw new CryptographicException("Data di attivazione della licenza non valida.");
        var maximum = issued.AddDays(30);
        // Older signed licenses already contain IssuedAt and receive the same 30-day limit.
        if (license.ExpiresAt is null) return maximum;
        if (!DateTimeOffset.TryParseExact(license.ExpiresAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiration)
            || expiration <= issued || expiration > maximum)
            throw new CryptographicException("Scadenza della licenza non valida.");
        return expiration;
    }

    public static byte[] UnwrapKey(LicensePayload payload, RSA device) =>
        device.Decrypt(Convert.FromBase64String(payload.WrappedKey), RSAEncryptionPadding.OaepSHA256);

    public static string DeviceHash(string publicKey) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey)));

    public static RSA ReadPublicKey(string publicKey)
    {
        byte[] key = Convert.FromBase64String(publicKey);
        RSA rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            if (consumed != key.Length || rsa.KeySize is < 2048 or > 4096)
                throw new CryptographicException("Chiave pubblica non valida.");
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static AesGcm CreateCipher(byte[] key)
    {
        if (key.Length != 32) throw new CryptographicException("È richiesta una chiave AES a 256 bit.");
        return new AesGcm(key, 16);
    }
}
