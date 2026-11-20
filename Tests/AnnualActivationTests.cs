using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PdfCustode.Core;
using PdfCustode.Server;
using PdfCustode.Server.Audit;
using PdfCustode.Server.SharePoint;

internal static class AnnualActivationTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "pdf-annual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var authority = RSA.Create(2048);
            using var device = RSA.Create(2048);
            string publicKey = Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
            await ProfilesAreValidatedBeforePersistence(Path.Combine(root, "validation"), authority, publicKey);
            await LastSlotIsSharedAcrossCatalogs(Path.Combine(root, "catalogs"), authority, publicKey);
            await LastSlotIsSharedAcrossProcesses(Path.Combine(root, "processes"), authority, publicKey);
            await ProfileAndLicenseSurviveRestartAndOutage(Path.Combine(root, "durable"), authority, publicKey);
            AnnualAllocationPreservesHistory(Path.Combine(root, "years"), authority, publicKey);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Annual quota and activation profile tests passed.");
    }

    public static void RunChild(string[] args)
    {
        string root = args[1], document = args[2];
        File.WriteAllText(Path.Combine(root, document + ".ready"), "ready");
        var waiting = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "start.signal")))
        {
            if (waiting.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Annual quota child not released.");
            Thread.Sleep(10);
        }
        try
        {
            var signed = new ActivationCatalog(root).Activate(new(args[3], document, args[4]), 1, quotaYear: 2026);
            Console.WriteLine(signed is null ? "REJECTED" : "ACTIVATED");
        }
        catch (LicenseQuotaExceededException) { Console.WriteLine("QUOTA"); }
    }

    private static async Task LastSlotIsSharedAcrossCatalogs(string root, RSA authority, string publicKey)
    {
        var requests = CreateTwoDocuments(root, authority, publicKey);
        using var start = new ManualResetEventSlim();
        var tasks = requests.Select(request => Task.Run(() =>
        {
            var catalog = new ActivationCatalog(root);
            start.Wait();
            try { return (License: catalog.Activate(request, 1, quotaYear: 2026), Quota: false); }
            catch (LicenseQuotaExceededException) { return (License: (SignedLicense?)null, Quota: true); }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        Assert(results.Count(value => value.License is not null) == 1 && results.Count(value => value.Quota) == 1,
            "the last annual quota slot must be shared atomically across documents and independent catalogs");
        int winner = Array.FindIndex(results, value => value.License is not null);
        var reopened = new ActivationCatalog(root);
        Assert(reopened.Activate(requests[winner], 0, quotaYear: 2027) == results[winner].License,
            "a new-year retry with zero quota must return the original signed license");
        Assert(reopened.GetSnapshots().SelectMany(s => s.Codes).Count(c => c.EffectiveQuotaYear == 2026) == 1,
            "retry must not move an activation into the new annual bucket");
    }

    private static async Task LastSlotIsSharedAcrossProcesses(string root, RSA authority, string publicKey)
    {
        var requests = CreateTwoDocuments(root, authority, publicKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var children = requests.Select(request =>
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { typeof(AnnualActivationTests).Assembly.Location, "--annual-quota-child", root, request.DocumentId, request.Code, publicKey })
                start.ArgumentList.Add(argument);
            return Process.Start(start) ?? throw new InvalidOperationException("Cannot start annual quota child.");
        }).ToArray();
        try
        {
            while (!requests.All(request => File.Exists(Path.Combine(root, request.DocumentId + ".ready"))))
                await Task.Delay(10, timeout.Token);
            File.WriteAllText(Path.Combine(root, "start.signal"), "start");
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync(timeout.Token)));
            var output = await Task.WhenAll(children.Select(child => child.StandardOutput.ReadToEndAsync()));
            var errors = await Task.WhenAll(children.Select(child => child.StandardError.ReadToEndAsync()));
            Assert(children.All(child => child.ExitCode == 0), "quota child processes must exit successfully: " + string.Join(";", errors));
            Assert(output.Count(value => value.Trim() == "ACTIVATED") == 1 && output.Count(value => value.Trim() == "QUOTA") == 1,
                "separate server processes must share the last annual quota slot");
            Assert(new ActivationCatalog(root).GetSnapshots().SelectMany(s => s.Codes).Count(c => c.License is not null) == 1,
                "cross-process quota rejection must leave only one durable activation");
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
                child.Dispose();
            }
        }
    }

    private static ActivationRequest[] CreateTwoDocuments(string root, RSA authority, string publicKey) =>
        new[] { "first", "second" }.Select(document =>
            new ActivationRequest(ActivationStore.Create(Path.Combine(root, document), document, new byte[32], authority), document, publicKey)).ToArray();

    private static async Task ProfilesAreValidatedBeforePersistence(string root, RSA authority, string publicKey)
    {
        string code = ActivationStore.Create(root, "validation", new byte[32], authority);
        var catalog = new ActivationCatalog(root);
        var coordinator = new ActivationCoordinator(catalog, UnavailableSynchronizer(root, catalog));
        byte[] before = File.ReadAllBytes(Path.Combine(root, "server-state.json"));
        var valid = ProfileRequest(code, "validation", publicKey);
        var invalid = new[]
        {
            new ActivationRequest(code, "validation", publicKey),
            valid with { Nome = "  " }, valid with { Nome = new string('N', 101) },
            valid with { Cognome = null }, valid with { Cognome = new string('C', 101) },
            valid with { CodiceUtente = "" }, valid with { CodiceUtente = new string('U', 129) },
            valid with { NomeMacchina = null }, valid with { NomeMacchina = new string('M', 256) },
            valid with { Piattaforma = "Linux" }, valid with { Piattaforma = null },
            valid with { Nome = "A\nB" }, valid with { Cognome = "A\tB" },
            valid with { CodiceUtente = "U\0" }, valid with { NomeMacchina = "M\r" },
            valid with { Piattaforma = "Windows\n" }
        };
        foreach (var request in invalid)
        {
            bool rejected = false;
            try { await coordinator.ActivateAsync(request, default); }
            catch (ActivationProfileValidationException error) { rejected = !string.IsNullOrWhiteSpace(error.Message); }
            Assert(rejected, "invalid fresh profiles must be rejected with a useful safe validation error before Graph");
            Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "server-state.json"))),
                "invalid profiles must not consume or rewrite their activation code");
        }
        Assert(await coordinator.ActivateAsync(valid with { Code = new string('0', 32) }, default) is null,
            "unknown codes must be rejected before unavailable Graph");
        Assert(await coordinator.ActivateAsync(valid with { DevicePublicKey = "invalid" }, default) is null,
            "invalid device keys must be rejected before unavailable Graph");
        Assert(await coordinator.ActivateAsync(valid with { DocumentId = "unknown" }, default) is null,
            "unknown documents must be rejected before unavailable Graph");
    }

    private static async Task ProfileAndLicenseSurviveRestartAndOutage(string root, RSA authority, string publicKey)
    {
        string code = ActivationStore.Create(root, "durable", new byte[32], authority);
        var clock = new AnnualClock(new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new ActivationCoordinator(new ActivationCatalog(root, clock));
        var request = ProfileRequest(code, "durable", publicKey) with { Nome = "  Jose\u0301 ", Cognome = " Rossi ", CodiceUtente = " U-1 ", NomeMacchina = " PC-1 " };
        var signed = await coordinator.ActivateAsync(request, default);
        Assert(signed is not null, "a valid profile must activate");
        var restarted = new ActivationCatalog(root, clock);
        var snapshot = restarted.GetSnapshots().Single().Codes.Single();
        Assert(snapshot.Profile == new ActivationProfile("José", "Rossi", "U-1", "PC-1", "Windows") && snapshot.QuotaYear == 2026,
            "normalized profile and annual bucket must survive restart with the signed license");
        var payload = JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(signed!.Payload))!;
        Assert(payload.IssuedAt == "2026-02-01T12:00:00.0000000+00:00" && payload.ExpiresAt == "2026-03-03T12:00:00.0000000+00:00",
            "the actual signed license must still expire exactly 30 days after activation");
        byte[] before = File.ReadAllBytes(Path.Combine(root, "server-state.json"));
        clock.Now = new(2027, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var offline = new ActivationCoordinator(restarted, UnavailableSynchronizer(root, restarted));
        Assert(await offline.ActivateAsync(new(code, "durable", publicKey), default) == signed,
            "expired same-device retry without a profile must survive Graph outage and a new year");
        Assert(await offline.ActivateAsync(request with { Nome = "Different", CodiceUtente = "changed", Piattaforma = "Android" }, default) == signed,
            "a repeated activation must return the original signed license");
        using var otherDevice = RSA.Create(2048);
        Assert(await offline.ActivateAsync(request with { DevicePublicKey = Convert.ToBase64String(otherDevice.ExportSubjectPublicKeyInfo()) }, default) is null,
            "profile retries must preserve device binding even while Graph is unavailable");
        Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "server-state.json"))),
            "retry must not overwrite the original profile, bucket or signed license");
        using var store = new ActivationStore(root);
        string next = store.IssueCode();
        var boundary = ProfileRequest(next, "durable", publicKey) with { Nome = new string('N', 100), Cognome = new string('C', 100), CodiceUtente = new string('U', 128), NomeMacchina = new string('M', 255), Piattaforma = "Android" };
        Assert(await coordinator.ActivateAsync(boundary, default) is not null, "valid profile maximum lengths and Android must be accepted");
    }

    private static void AnnualAllocationPreservesHistory(string root, RSA authority, string publicKey)
    {
        string legacyFolder = Path.Combine(root, "legacy");
        string oldCode = ActivationStore.Create(legacyFolder, "legacy", new byte[32], authority);
        // Local calendar year 2026, UTC year 2025: legacy accounting must use UTC.
        var oldClock = new AnnualClock(new(2026, 1, 1, 0, 30, 0, TimeSpan.FromHours(1)));
        SignedLicense oldLicense;
        string renewal;
        using (var oldStore = new ActivationStore(legacyFolder, oldClock))
        {
            oldLicense = oldStore.Activate(new(oldCode, "legacy", publicKey))!;
            renewal = oldStore.IssueCode();
        }
        string statePath = Path.Combine(legacyFolder, "server-state.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        foreach (var entry in state["Codes"]!.AsArray())
        {
            entry!.AsObject().Remove("Profile"); entry.AsObject().Remove("QuotaYear"); entry.AsObject().Remove("ActivationCode");
        }
        File.WriteAllText(statePath, state.ToJsonString());
        var currentClock = new AnnualClock(new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var catalog = new ActivationCatalog(root, currentClock);
        var legacy = catalog.GetSnapshots().Single().Codes;
        Assert(legacy[0].EffectiveQuotaYear == 2025 && legacy[0].Profile is null && legacy[1].EffectiveQuotaYear is null,
            "legacy signed entries fall back to IssuedAt UTC year and unused legacy codes have no quota year");
        Assert(catalog.Activate(new(oldCode, "legacy", publicKey), 0, quotaYear: 2026) == oldLicense,
            "legacy retry remains usable without migration or a new quota slot");
        var renewed = catalog.Activate(new(renewal, "legacy", publicKey), 1, quotaYear: 2026);
        Assert(renewed is not null && renewed != oldLicense, "a new renewal code consumes the new year's allocation");
        currentClock.Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        using var currentStore = new ActivationStore(legacyFolder);
        var next = new ActivationRequest(currentStore.IssueCode(), "legacy", publicKey);
        QuotaRejected(() => catalog.Activate(next, 1, quotaYear: 2026), "expired signed licenses must continue consuming their original annual slot");
        Assert(catalog.Activate(next, 1, quotaYear: 2027) is not null, "a different annual bucket must have an independent allocation");
        Assert(catalog.Activate(new(renewal, "legacy", publicKey), 0, quotaYear: 2027) == renewed,
            "new-year retry must not renew or reassign an existing activation");
        var final = new ActivationCatalog(root).GetSnapshots().Single().Codes;
        Assert(final.Select(code => code.EffectiveQuotaYear).SequenceEqual(new int?[] { 2025, 2026, 2027 }),
            "restart must preserve exactly one historic activation in each annual bucket");
    }

    private static ActivationRequest ProfileRequest(string code, string document, string key) =>
        new(code, document, key, "Mario", "Rossi", "U-1", "PC-1", "Windows");

    private static SharePointSynchronizer UnavailableSynchronizer(string root, ActivationCatalog catalog) =>
        new(new UnavailableGraph(), new SharePointOptions { QuotaYear = 2026 }, catalog,
            new AuditJournal(Path.Combine(root, ".audit")), NullLogger<SharePointSynchronizer>.Instance);

    private static void QuotaRejected(Action action, string message)
    {
        try { action(); }
        catch (LicenseQuotaExceededException) { return; }
        throw new Exception(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class AnnualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class UnavailableGraph : IGraphLists
    {
        public Task<IReadOnlyList<GraphColumn>> GetColumnsAsync(string list, CancellationToken ct) => throw new HttpRequestException("Graph unavailable");
        public Task<IReadOnlyList<GraphItem>> GetItemsAsync(string list, CancellationToken ct) => throw new HttpRequestException("Graph unavailable");
        public Task<GraphItem?> FindAsync(string list, string field, string value, CancellationToken ct) => throw new HttpRequestException("Graph unavailable");
        public Task<GraphItem> CreateAsync(string list, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) => throw new HttpRequestException("Graph unavailable");
        public Task UpdateAsync(string list, string id, IReadOnlyDictionary<string, object?> fields, string? etag, CancellationToken ct) => throw new HttpRequestException("Graph unavailable");
    }
}
