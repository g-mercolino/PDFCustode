using Android.Security.Keystore;
using Java.Security;
using Java.Security.Spec;
using Javax.Crypto;
using Javax.Crypto.Spec;
using PdfCustode.Core;

namespace PdfCustode.App;

internal sealed class DeviceKey : IDisposable
{
    private const string Alias = "pdfcustode-device-v1";
    private readonly KeyStore store;

    public DeviceKey()
    {
        store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.ContainsAlias(Alias))
            return;

        using var generator = KeyPairGenerator.GetInstance("RSA", "AndroidKeyStore")!;
        using var builder = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Decrypt);
        using var spec = builder.SetKeySize(2048)
            .SetDigests("SHA-256")
            .SetMgf1Digests("SHA-256")
            .SetEncryptionPaddings("OAEPPadding")
            .Build();
        generator.Initialize(spec);
        using var pair = generator.GenerateKeyPair();
    }

    public string PublicKey
    {
        get
        {
            using var certificate = store.GetCertificate(Alias)!;
            using var publicKey = certificate.PublicKey!;
            return Convert.ToBase64String(publicKey.GetEncoded()!);
        }
    }

    public byte[] Unwrap(LicensePayload license)
    {
        var privateKey = store.GetKey(Alias, null)!;
        using var cipher = Cipher.GetInstance("RSA/ECB/OAEPWithSHA-256AndMGF1Padding", "AndroidKeyStoreBCWorkaround")!;
        using var parameters = new OAEPParameterSpec("SHA-256", "MGF1", MGF1ParameterSpec.Sha256,
            PSource.PSpecified.Default);
        cipher.Init(CipherMode.DecryptMode, privateKey, parameters);
        return cipher.DoFinal(Convert.FromBase64String(license.WrappedKey))!;
    }

    public void Dispose() => store.Dispose();
}

