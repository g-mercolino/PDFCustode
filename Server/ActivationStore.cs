using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfCustode.Core;

namespace PdfCustode.Server;

public sealed class ActivationStore(string folder) : IDisposable
{
    private readonly string stateFile = Path.Combine(folder, "server-state.json");
    private readonly string keyFile = Path.Combine(folder, "authority-private.pem");
    private readonly string lockFile = Path.Combine(folder, "server-state.lock");
    private readonly Lock gate = new();

    public static string Create(string folder, string documentId, byte[] key, RSA authority)
    {
        Directory.CreateDirectory(folder);
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using (var stream = new FileStream(Path.Combine(folder, "authority-private.pem"), FileMode.CreateNew))
        using (var writer = new StreamWriter(stream))
            writer.Write(authority.ExportPkcs8PrivateKeyPem());
        WriteState(Path.Combine(folder, "server-state.json"),
            new(documentId, Convert.ToBase64String(key), [new(CodeHash(code), null, null)]), overwrite: false);
        return code;
    }

    public SignedLicense? Activate(ActivationRequest request)
    {
        if (request.Code is not { Length: 32 } || !request.Code.All(Uri.IsHexDigit) ||
            request.DocumentId is not { Length: > 0 and <= 128 } ||
            request.DevicePublicKey is not { Length: > 0 and <= 1024 })
            return null;

        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            State state = ReadState();
            if (state.DocumentId != request.DocumentId) return null;
            byte[] suppliedHash = HashCodeText(request.Code.ToUpperInvariant());
            int index = state.Codes.FindIndex(entry => CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(entry.CodeHash), suppliedHash));
            if (index < 0) return null;
            CodeEntry entry = state.Codes[index];
            using RSA? device = ReadDeviceKey(request.DevicePublicKey);
            if (device is null) return null;
            string hash = Crypto.DeviceHash(request.DevicePublicKey);
            if (entry.License is not null)
                return entry.DeviceKeyHash == hash ? entry.License : null;

            using RSA authority = RSA.Create();
            authority.ImportFromPem(File.ReadAllText(keyFile));
            byte[] key = Convert.FromBase64String(state.DocumentKey);
            SignedLicense signed;
            try
            {
                string wrapped = Convert.ToBase64String(device.Encrypt(key, RSAEncryptionPadding.OaepSHA256));
                signed = Crypto.Sign(new(Guid.NewGuid().ToString("N"), state.DocumentId, hash, wrapped,
                    DateTimeOffset.UtcNow.ToString("O")), authority);
            }
            finally { CryptographicOperations.ZeroMemory(key); }

            state.Codes[index] = entry with { DeviceKeyHash = hash, License = signed };
            WriteState(stateFile, state, overwrite: true);
            return signed;
        }
    }

    public string IssueCode()
    {
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            State state = ReadState();
            string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            state.Codes.Add(new(CodeHash(code), null, null));
            WriteState(stateFile, state, overwrite: true);
            return code;
        }
    }

    private State ReadState() =>
        JsonSerializer.Deserialize<State>(File.ReadAllBytes(stateFile))
        ?? throw new InvalidDataException("Stato di attivazione non valido.");

    public void Dispose() { }

    private FileStream AcquireFileLock()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }

    private static RSA? ReadDeviceKey(string publicKey)
    {
        try { return Crypto.ReadPublicKey(publicKey); }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return null; }
    }

    private static string CodeHash(string code) =>
        Convert.ToHexString(HashCodeText(code));

    private static byte[] HashCodeText(string code) => SHA256.HashData(Encoding.ASCII.GetBytes(code));

    private static void WriteState(string path, State state, bool overwrite)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, state);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }

    private sealed record State(string DocumentId, string DocumentKey, List<CodeEntry> Codes);
    private sealed record CodeEntry(string CodeHash, string? DeviceKeyHash, SignedLicense? License);
}

