using System.Security.Cryptography;

namespace PdfCustode.Core;

public sealed record DocumentPackage(int Version, EncryptedDocument Document, string Authority)
{
    public void Validate()
    {
        if (Version != 1 || Document is null || string.IsNullOrWhiteSpace(Document.DocumentId)
            || Document.DocumentId.Length > 128 || string.IsNullOrWhiteSpace(Document.Title)
            || string.IsNullOrWhiteSpace(Authority))
            throw new InvalidDataException("File PDF Custode non valido o versione non supportata.");
        using var key = Crypto.ReadPublicKey(Authority);
        if (Convert.FromBase64String(Document.Nonce).Length != 12
            || Convert.FromBase64String(Document.Tag).Length != 16
            || string.IsNullOrEmpty(Document.Ciphertext))
            throw new InvalidDataException("Documento cifrato non valido.");
        _ = Convert.FromBase64String(Document.Ciphertext);
    }
}
