using PdfCustode.Core;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace PdfCustode.Server;

public sealed class ActivationCatalog(string root, TimeProvider? clock = null)
{
    private readonly string dataRoot = Path.GetFullPath(root);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".audit", "audit", "logs", "pending", "history", "acknowledged", ".git"
    };

    public SignedLicense? Activate(ActivationRequest request, int? maxActivations = null,
        int? quotaYear = null, ActivationProfile? profile = null)
    {
        if (maxActivations < 0) throw new ArgumentOutOfRangeException(nameof(maxActivations));
        if (quotaYear is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(quotaYear));
        if (!ActivationStore.IsValidRequest(request)) return null;
        try { using var device = Crypto.ReadPublicKey(request.DevicePublicKey); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
        // Every catalog writer sharing this data root serializes the annual count and durable claim.
        using var annualLock = AcquireAnnualLock();
        var snapshots = GetSnapshots();
        var document = snapshots.SingleOrDefault(value => value.DocumentId == request.DocumentId);
        if (document is null) return null;
        byte[] codeHash = SHA256.HashData(Encoding.ASCII.GetBytes(request.Code.ToUpperInvariant()));
        var candidate = document.Codes.SingleOrDefault(value => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(value.CodeHash), codeHash));
        if (candidate is null) return null;
        if (candidate.License is not null)
            return candidate.DeviceKeyHash == Crypto.DeviceHash(request.DevicePublicKey) ? candidate.License : null;
        int year = quotaYear ?? time.GetUtcNow().UtcDateTime.Year;
        if (maxActivations is int maximum && snapshots.Sum(value => value.Codes.Count(code => code.EffectiveQuotaYear == year)) >= maximum)
            throw new LicenseQuotaExceededException();
        using var store = new ActivationStore(document.Folder, time);
        return store.Activate(request, profile: profile, quotaYear: year);
    }

    public SignedLicense? TryGetExisting(ActivationRequest request)
    {
        var document = FindDocument(request.DocumentId);
        if (document is null) return null;
        using var store = new ActivationStore(document.Folder);
        return store.TryGetExisting(request);
    }

    public IReadOnlyList<DocumentSnapshot> GetSnapshots()
    {
        var result = new List<DocumentSnapshot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in DocumentFolders())
        {
            using var store = new ActivationStore(folder);
            var snapshot = store.GetSnapshot();
            if (!ids.Add(snapshot.DocumentId))
                throw new InvalidDataException("Identificativo documento duplicato nell'archivio amministrativo.");
            result.Add(snapshot);
        }
        return result;
    }

    private DocumentSnapshot? FindDocument(string? documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || documentId.Length > 128) return null;
        return GetSnapshots().SingleOrDefault(document => document.DocumentId == documentId);
    }

    private IEnumerable<string> DocumentFolders()
    {
        var remaining = new Stack<string>();
        remaining.Push(dataRoot);
        while (remaining.TryPop(out string? folder))
        {
            if (File.Exists(Path.Combine(folder, "server-state.json"))) yield return folder;
            foreach (string child in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
            {
                if (ExcludedFolders.Contains(Path.GetFileName(child)) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                remaining.Push(child);
            }
        }
    }

    private FileStream AcquireAnnualLock()
    {
        long started = Stopwatch.GetTimestamp();
        for (;;)
        {
            try { return new FileStream(Path.Combine(dataRoot, "annual-quota.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30)) { Thread.Sleep(25); }
        }
    }
}
