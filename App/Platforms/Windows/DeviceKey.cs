using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PdfCustode.Core;

namespace PdfCustode.App;

internal sealed class DeviceKey : IDisposable
{
    private readonly RSACng rsa;

    public DeviceKey()
    {
        const string name = "PDF Custode Device Key v1";
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        using var key = CngKey.Exists(name, provider)
            ? CngKey.Open(name, provider)
            : CngKey.Create(CngAlgorithm.Rsa, name, new CngKeyCreationParameters
            {
                Provider = provider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Decryption,
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) }
            });
        rsa = new RSACng(key);
    }

    public string PublicKey => Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

    public byte[] Unwrap(LicensePayload license) => Crypto.UnwrapKey(license, rsa);

    public void Dispose() => rsa.Dispose();
}

internal static partial class WindowsProtection
{
    [LibraryImport("user32.dll", EntryPoint = "SetWindowDisplayAffinity")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);

    public static void ExcludeFromCapture(nint window) => SetWindowDisplayAffinity(window, 0x11);
}
