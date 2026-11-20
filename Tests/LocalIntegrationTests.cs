using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using PdfCustode.Core;
using PdfCustode.Server;
using PdfCustode.Server.Audit;

public static class LocalIntegrationTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "pdfcustode-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using RSA authority = RSA.Create(2048);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            string folder = Path.Combine(root, "document");
            string first = ActivationStore.Create(folder, "recoverable", key, authority);
            using var store = new ActivationStore(folder);
            string second = store.IssueCode();
            using var state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "server-state.json")));
            var saved = state.RootElement.GetProperty("Codes").EnumerateArray().ToArray();
            Assert(saved[0].TryGetProperty("ActivationCode", out var firstValue) && firstValue.GetString() == first,
                "first code must be recoverable from private state");
            Assert(saved[1].TryGetProperty("ActivationCode", out var secondValue) && secondValue.GetString() == second,
                "issued code must be recoverable from private state");
            await QuotaIsAtomicAcrossStores(root, authority, key);
            CatalogRefusesAmbiguousDocuments(root, authority, key);
            await SnapshotsUseFileLock(root, authority, key);
            await AuditSurvivesRestartAndAcknowledgement(root);
            AuditPendingIsBounded(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AuditPendingIsBounded(string root)
    {
        string journalRoot = Path.Combine(root, ".audit-bounded");
        var journal = new AuditJournal(journalRoot);
        string historyPath = Path.Combine(journalRoot, "history", "2026-06-02.jsonl");
        var expectedPending = new List<AuditEvent>();
        // Bulk-write a real persisted journal fixture: many acknowledged history entries and a
        // small remote-outage backlog. Setup deliberately excludes per-event fsync timing.
        using (var history = new StreamWriter(historyPath))
        {
            for (int number = 0; number < 50000; number++)
            {
                var item = new AuditEvent((number + 1).ToString("X32"), new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero).AddSeconds(number < 225 ? 225 - number : 0),
                    "Attivazione", "Successo", "Historical event", "document", "license", "request");
                string json = JsonSerializer.Serialize(item);
                history.WriteLine(json);
                if (number < 225)
                {
                    File.WriteAllText(Path.Combine(journalRoot, "pending", item.EventId.ToLowerInvariant() + ".json"), json);
                    expectedPending.Add(item);
                }
            }
        }
        var elapsed = Stopwatch.StartNew();
        var pending = journal.Pending();
        elapsed.Stop();
        Assert(pending.Count == 200, "pending replay must load a bounded default batch during remote outages");
        Assert(pending.Select(item => item.EventId).SequenceEqual(expectedPending.OrderBy(item => item.DataEvento).Take(200).Select(item => item.EventId.ToLowerInvariant())),
            "oldest audit events must be selected before limiting a pending batch");
        Assert(elapsed.Elapsed < TimeSpan.FromSeconds(10), "pending replay must index daily history once per batch instead of once per event");
        var smaller = journal.Pending(10);
        Assert(smaller.Count == 10, "caller-selected smaller pending batches must be honored");
        foreach (var item in smaller) journal.Acknowledge(item.EventId);
        Assert(journal.Pending(1000).Count == 215, "acknowledging a bounded batch must leave later pending events available");
    }

    private static async Task SnapshotsUseFileLock(string root, RSA authority, byte[] key)
    {
        string folder = Path.Combine(root, "snapshot");
        string code = ActivationStore.Create(folder, "snapshot", key, authority);
        using RSA device = RSA.Create(2048);
        string publicKey = Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
        var request = new ActivationRequest(code, "snapshot", publicKey);
        var catalog = new ActivationCatalog(folder);
        Assert(catalog.TryGetExisting(request) is null && catalog.GetSnapshots().Single().Codes.Single().License is null,
            "existing-license lookup must not consume an unused code");
        var signed = catalog.Activate(request, 1);
        Assert(signed is not null && catalog.TryGetExisting(request) == signed,
            "catalog must recover the issued signed license without quota consumption");
        using var store = new ActivationStore(folder);
        using var entered = new ManualResetEventSlim();
        Task<DocumentSnapshot> waiting;
        using (var externalLock = new FileStream(Path.Combine(folder, "server-state.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            waiting = Task.Factory.StartNew(() => { entered.Set(); return store.GetSnapshot(); }, TaskCreationOptions.LongRunning);
            Assert(entered.Wait(TimeSpan.FromSeconds(2)), "snapshot task must start");
            await Task.Delay(75);
            Assert(!waiting.IsCompleted, "snapshot read must honor the exclusive file lock");
        }
        var snapshot = await waiting;
        Assert(snapshot.Folder == Path.GetFullPath(folder) && snapshot.DocumentId == "snapshot" &&
            snapshot.Codes.Single().ActivationCode == code && snapshot.Codes.Single().License == signed,
            "snapshot must expose recoverable administrative code and signed license without private keys");
    }

    private static async Task AuditSurvivesRestartAndAcknowledgement(string root)
    {
        string journalRoot = Path.Combine(root, ".audit");
        string eventId = Guid.NewGuid().ToString("N");
        string documentId = Guid.NewGuid().ToString("N");
        string licenseId = Guid.NewGuid().ToString("N");
        string secretCode = "1234567890ABCDEF1234567890ABCDEF";
        string secretToken = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWNyZXQifQ.signatureHidden";
        var item = new AuditEvent(eventId, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), "Attivazione", "Successo",
            "Activated\n" + secretCode + " Bearer " + secretToken, documentId, licenseId, "request-1",
            JsonSerializer.Serialize(new { reason = secretCode, statusCode = 200, ActivationCode = secretCode, access_token = secretToken, WrappedKey = "private" }));
        new AuditJournal(journalRoot).Record(item);
        var recovered = new AuditJournal(journalRoot).Pending();
        Assert(recovered.Count == 1 && recovered[0].EventId == eventId, "pending audit event must survive restart");
        Assert(recovered[0].DocumentId == documentId && recovered[0].LicenseId == licenseId,
            "legitimate document and license GUIDs must not be redacted");
        Assert(!recovered[0].Title.Contains(secretCode) && !recovered[0].Title.Contains(secretToken) && !recovered[0].Title.Contains('\n'),
            "audit titles must redact activation codes, bearer tokens and control characters");
        using (var details = JsonDocument.Parse(recovered[0].Details!))
        {
            Assert(details.RootElement.GetProperty("statusCode").GetInt32() == 200, "safe audit details must remain useful");
            Assert(!details.RootElement.TryGetProperty("ActivationCode", out _) && !details.RootElement.TryGetProperty("access_token", out _) &&
                !details.RootElement.TryGetProperty("WrappedKey", out _) && !recovered[0].Details!.Contains(secretCode),
                "audit details must persist only allowed sanitized keys");
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new AuditJournal(journalRoot).Record(item))));
        Assert(new AuditJournal(journalRoot).Pending().Count == 1, "recording the same event concurrently must be idempotent");
        string history = Directory.GetFiles(Path.Combine(journalRoot, "history"), "*.jsonl").Single();
        Assert(File.ReadAllLines(history).Length == 1, "one event must produce one daily history line");

        // A crash after the pending commit but before the history append must be replayed.
        File.Delete(history);
        Assert(new AuditJournal(journalRoot).Pending().Count == 1 && File.ReadAllLines(history).Length == 1,
            "pending recovery must repair a missing history append");
        string pendingFile = Directory.GetFiles(Path.Combine(journalRoot, "pending"), "*.json").Single();
        byte[] pendingBytes = File.ReadAllBytes(pendingFile);
        var acknowledger = new AuditJournal(journalRoot);
        acknowledger.Acknowledge(eventId);
        Assert(new AuditJournal(journalRoot).Pending().Count == 0, "acknowledged event must stay acknowledged after restart");
        new AuditJournal(journalRoot).Record(item with { Title = "retry changed" });
        Assert(new AuditJournal(journalRoot).Pending().Count == 0 && File.ReadAllLines(history).Length == 1,
            "recording an acknowledged event must never requeue or duplicate it");

        // A crash between the acknowledgement commit and pending deletion leaves this shape.
        File.WriteAllBytes(pendingFile, pendingBytes);
        Assert(new AuditJournal(journalRoot).Pending().Count == 0, "acknowledgement marker must win over a stale pending file");
        string sentinel = Path.Combine(root, "sentinel.json");
        File.WriteAllText(sentinel, "keep");
        bool invalidId = false;
        try { acknowledger.Acknowledge("../../sentinel"); }
        catch (ArgumentException) { invalidId = true; }
        Assert(invalidId && File.ReadAllText(sentinel) == "keep", "acknowledgement ids must not permit path traversal");

        File.AppendAllText(history, "{\"EventId\":\"incomplete");
        string secondId = Guid.NewGuid().ToString("N");
        var second = item with { EventId = secondId, Title = new string('x', 10000), Details = new string('s', 10000) };
        acknowledger.Record(second);
        var secondPending = new AuditJournal(journalRoot).Pending().Single();
        Assert(secondPending.EventId == secondId && secondPending.Title.Length <= 256 && (secondPending.Details?.Length ?? 0) <= 2048,
            "untrusted audit text must have bounded persisted size");
        var lines = File.ReadAllLines(history);
        Assert(lines.Length == 2, "a torn trailing history append must be repaired without losing committed lines");
        foreach (string line in lines) using (JsonDocument.Parse(line)) { }
        var newEvents = Enumerable.Range(0, 5).Select(number => item with { EventId = Guid.NewGuid().ToString("N"), Title = "Event " + number }).ToArray();
        await Task.WhenAll(newEvents.Select(entry => Task.Run(() => new AuditJournal(journalRoot).Record(entry))));
        Assert(new AuditJournal(journalRoot).Pending().Count == 6 && File.ReadAllLines(history).Length == 7,
            "concurrent distinct audit records must not lose pending events or history lines");
    }

    private static void CatalogRefusesAmbiguousDocuments(string root, RSA authority, byte[] key)
    {
        string catalogRoot = Path.Combine(root, "catalog");
        string first = ActivationStore.Create(Path.Combine(catalogRoot, "first", "server"), "duplicate", key, authority);
        ActivationStore.Create(Path.Combine(catalogRoot, "second", "server"), "duplicate", key, authority);
        using RSA device = RSA.Create(2048);
        string publicKey = Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
        var catalog = new ActivationCatalog(catalogRoot);
        bool duplicateRejected = false;
        try { catalog.Activate(new(first, "duplicate", publicKey)); }
        catch (InvalidDataException) { duplicateRejected = true; }
        Assert(duplicateRejected, "duplicate document ids must fail safely instead of choosing an arbitrary authority");

        string hiddenRoot = Path.Combine(root, "hidden-audit");
        string hiddenCode = ActivationStore.Create(Path.Combine(hiddenRoot, ".audit", "archived"), "hidden", key, authority);
        Assert(new ActivationCatalog(hiddenRoot).Activate(new(hiddenCode, "hidden", publicKey)) is null,
            "audit archives must not be searched for activation documents");
    }

    private static async Task QuotaIsAtomicAcrossStores(string root, RSA authority, byte[] key)
    {
        using RSA device = RSA.Create(2048);
        using RSA other = RSA.Create(2048);
        string publicKey = Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
        string otherKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        string folder = Path.Combine(root, "quota");
        string firstCode = ActivationStore.Create(folder, "quota", key, authority);
        using var firstStore = new ActivationStore(folder);
        string secondCode = firstStore.IssueCode();
        var requests = new[] { new ActivationRequest(firstCode, "quota", publicKey), new ActivationRequest(secondCode, "quota", publicKey) };
        using var start = new ManualResetEventSlim();
        var tasks = requests.Select(request => Task.Run(() =>
        {
            using var separateStore = new ActivationStore(folder);
            start.Wait();
            try { return (License: separateStore.Activate(request, 1), Quota: false); }
            catch (LicenseQuotaExceededException) { return (License: (SignedLicense?)null, Quota: true); }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        Assert(results.Count(value => value.License is not null) == 1 && results.Count(value => value.Quota) == 1,
            "quota check and claim must be atomic across separate store instances");
        int winner = Array.FindIndex(results, value => value.License is not null);
        using var reopened = new ActivationStore(folder);
        Assert(reopened.Activate(requests[winner], 0) == results[winner].License,
            "existing device retry must succeed after restart even with exhausted quota");
        Assert(reopened.Activate(requests[winner] with { DevicePublicKey = otherKey }, 0) is null,
            "quota exhaustion must not bypass the device binding");

        string legacyFolder = Path.Combine(root, "legacy");
        string legacyCode = ActivationStore.Create(legacyFolder, "legacy", key, authority);
        string legacyPath = Path.Combine(legacyFolder, "server-state.json");
        string codeHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(legacyCode)));
        File.WriteAllBytes(legacyPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            DocumentId = "legacy", DocumentKey = Convert.ToBase64String(key),
            Codes = new[] { new { CodeHash = codeHash, DeviceKeyHash = (string?)null, License = (SignedLicense?)null } }
        }));
        using var legacy = new ActivationStore(legacyFolder);
        var legacyRequest = new ActivationRequest(legacyCode, "legacy", publicKey);
        var oldLicense = legacy.Activate(legacyRequest, 1);
        Assert(oldLicense is not null && legacy.Activate(legacyRequest, 0) == oldLicense,
            "old state without recoverable codes must remain usable");

        string expiredFolder = Path.Combine(root, "expired-quota");
        string expiredCode = ActivationStore.Create(expiredFolder, "expired", key, authority);
        var clock = new LocalClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var expiredStore = new ActivationStore(expiredFolder, clock);
        Assert(expiredStore.Activate(new(expiredCode, "expired", publicKey), 1) is not null, "initial quota slot must be usable");
        clock.Now = clock.Now.AddDays(31);
        string replacementCode = expiredStore.IssueCode();
        bool rejected = false;
        try { expiredStore.Activate(new(replacementCode, "expired", publicKey), 1); }
        catch (LicenseQuotaExceededException) { rejected = true; }
        Assert(rejected, "expired signed licenses still consume quota");
    }

    private static void Assert(bool passed, string name)
    {
        if (!passed) throw new Exception(name);
    }

    private sealed class LocalClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
