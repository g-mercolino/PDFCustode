using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using PdfCustode.Core;

namespace PdfCustode.Server;

public sealed class LicenseQuotaExceededException() : InvalidOperationException("La distribuzione ha esaurito le licenze disponibili.");

public sealed record ActivationSnapshot(string CodeHash, string? DeviceKeyHash, SignedLicense? License, string? ActivationCode,
    ActivationProfile? Profile = null, int? QuotaYear = null)
{
    public int? EffectiveQuotaYear
    {
        get
        {
            if (License is null) return null;
            if (QuotaYear is int year) return year;
            var payload = JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(License.Payload))
                ?? throw new InvalidDataException("Licenza locale non valida.");
            return DateTimeOffset.ParseExact(payload.IssuedAt, "O", CultureInfo.InvariantCulture).UtcDateTime.Year;
        }
    }
}
public sealed record DocumentSnapshot(string Folder, string DocumentId, IReadOnlyList<ActivationSnapshot> Codes);

public sealed class ActivationStore : IDisposable
{
    private readonly string folder;
    private readonly TimeProvider clock;
    private readonly string stateFile;
    private readonly string keyFile;
    private readonly string lockFile;
    private readonly Lock gate = new();

    public ActivationStore(string folder, TimeProvider? clock = null)
    {
        this.folder = Path.GetFullPath(folder);
        this.clock = clock ?? TimeProvider.System;
        stateFile = Path.Combine(this.folder, "server-state.json");
        keyFile = Path.Combine(this.folder, "authority-private.pem");
        lockFile = Path.Combine(this.folder, "server-state.lock");
    }

    public static string Create(string folder, string documentId, byte[] key, RSA authority)
    {
        Directory.CreateDirectory(folder);
        using var fileLock = AcquireFileLock(Path.Combine(folder, "server-state.lock"));
        if (File.Exists(Path.Combine(folder, "server-state.json")))
            throw new IOException("Stato di attivazione già presente.");
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using (var stream = new FileStream(Path.Combine(folder, "authority-private.pem"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(authority.ExportPkcs8PrivateKeyPem()));
            stream.Flush(flushToDisk: true);
        }
        WriteState(Path.Combine(folder, "server-state.json"),
            new(documentId, Convert.ToBase64String(key), [new(CodeHash(code), null, null, code)]), overwrite: false);
        return code;
    }

    public SignedLicense? Activate(ActivationRequest request, int? maxActivations = null,
        ActivationProfile? profile = null, int? quotaYear = null)
    {
        if (maxActivations < 0) throw new ArgumentOutOfRangeException(nameof(maxActivations));
        if (quotaYear is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(quotaYear));
        if (!IsValidRequest(request)) return null;

        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            State state = ReadState();
            if (state.DocumentId != request.DocumentId) return null;
            int index = FindCode(state, request.Code);
            if (index < 0) return null;
            CodeEntry entry = state.Codes[index];
            using RSA? device = ReadDeviceKey(request.DevicePublicKey);
            if (device is null) return null;
            string hash = Crypto.DeviceHash(request.DevicePublicKey);
            if (entry.License is not null)
                return entry.DeviceKeyHash == hash ? entry.License : null;
            if (maxActivations is int maximum && state.Codes.Count(code => code.License is not null) >= maximum)
                throw new LicenseQuotaExceededException();

            using RSA authority = RSA.Create();
            authority.ImportFromPem(File.ReadAllText(keyFile));
            byte[] key = Convert.FromBase64String(state.DocumentKey);
            SignedLicense signed;
            try
            {
                string wrapped = Convert.ToBase64String(device.Encrypt(key, RSAEncryptionPadding.OaepSHA256));
                var issued = clock.GetUtcNow();
                signed = Crypto.Sign(new(Guid.NewGuid().ToString("N"), state.DocumentId, hash, wrapped,
                    issued.ToString("O"), issued.AddDays(30).ToString("O")), authority);
            }
            finally { CryptographicOperations.ZeroMemory(key); }

            state.Codes[index] = entry with { DeviceKeyHash = hash, License = signed, Profile = profile, QuotaYear = quotaYear };
            WriteState(stateFile, state, overwrite: true);
            return signed;
        }
    }

    public SignedLicense? TryGetExisting(ActivationRequest request)
    {
        if (!IsValidRequest(request)) return null;
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            State state = ReadState();
            if (state.DocumentId != request.DocumentId) return null;
            int index = FindCode(state, request.Code);
            if (index < 0 || state.Codes[index].License is null) return null;
            using RSA? device = ReadDeviceKey(request.DevicePublicKey);
            if (device is null) return null;
            CodeEntry entry = state.Codes[index];
            return entry.DeviceKeyHash == Crypto.DeviceHash(request.DevicePublicKey) ? entry.License : null;
        }
    }

    public DocumentSnapshot GetSnapshot()
    {
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            State state = ReadState();
            return new(folder, state.DocumentId, state.Codes.Select(entry =>
                new ActivationSnapshot(entry.CodeHash, entry.DeviceKeyHash, entry.License, entry.ActivationCode, entry.Profile, entry.QuotaYear)).ToArray());
        }
    }

    public string IssueCode()
    {
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            State state = ReadState();
            string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            state.Codes.Add(new(CodeHash(code), null, null, code));
            WriteState(stateFile, state, overwrite: true);
            return code;
        }
    }

    private State ReadState()
    {
        var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(stateFile));
        if (state is null || string.IsNullOrWhiteSpace(state.DocumentId) || state.DocumentId.Length > 128 || state.Codes is null ||
            state.Codes.Any(entry => entry is null || entry.CodeHash is not { Length: 64 } || !entry.CodeHash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Stato di attivazione non valido.");
        return state;
    }

    public void Dispose() { }

    private FileStream AcquireFileLock() => AcquireFileLock(lockFile);

    private static FileStream AcquireFileLock(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }

    internal static bool IsValidRequest(ActivationRequest request) =>
        request.Code is { Length: 32 } && request.Code.All(Uri.IsHexDigit) &&
        request.DocumentId is { Length: > 0 and <= 128 } &&
        request.DevicePublicKey is { Length: > 0 and <= 1024 };

    private static int FindCode(State state, string code)
    {
        byte[] suppliedHash = HashCodeText(code.ToUpperInvariant());
        return state.Codes.FindIndex(entry => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.CodeHash), suppliedHash));
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
    private sealed record CodeEntry(string CodeHash, string? DeviceKeyHash, SignedLicense? License, string? ActivationCode = null,
        ActivationProfile? Profile = null, int? QuotaYear = null);
}

