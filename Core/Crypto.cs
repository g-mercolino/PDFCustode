using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfCustode.Core;

public sealed record EncryptedDocument(string DocumentId, string Title, string Nonce, string Ciphertext, string Tag);
public sealed record ActivationRequest(string Code, string DocumentId, string DevicePublicKey);
public sealed record LicensePayload(string LicenseId, string DocumentId, string DeviceKeyHash, string WrappedKey, string IssuedAt);
public sealed record SignedLicense(string Payload, string Signature);

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

    public static LicensePayload Verify(SignedLicense license, string publicKey, string documentId, string devicePublicKey)
    {
        using var authority = ReadPublicKey(publicKey);
        byte[] payload = Convert.FromBase64String(license.Payload);
        if (!authority.VerifyData(payload, Convert.FromBase64String(license.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Firma della licenza non valida.");
        var decoded = JsonSerializer.Deserialize<LicensePayload>(payload);
        if (decoded is null || decoded.DocumentId != documentId || decoded.DeviceKeyHash != DeviceHash(devicePublicKey))
            throw new CryptographicException("La licenza non corrisponde al documento o al dispositivo.");
        return decoded;
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
