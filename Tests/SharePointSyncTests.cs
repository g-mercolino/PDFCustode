using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PdfCustode.Core;
using PdfCustode.Server;
using PdfCustode.Server.Audit;
using PdfCustode.Server.SharePoint;

internal static class SharePointSyncTests
{
    public static async Task RunAsync()
    {
        await ActivatedRowsAndAnnualQuota();
        await HistoricalYears();
        await ConcurrentConsumptionRefresh();
        await SchemaPreflight();
        await InternalNamesWinOverDisplayAliases();
        await ConflictAndOutageRecovery();
        Console.WriteLine("SharePoint annual projection tests passed.");
    }

    // Catches projecting unused codes, document-specific totals and overwriting administrative dates.
    private static async Task ActivatedRowsAndAnnualQuota()
    {
        using var f = new Fixture(new FixedClock(new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero)));
        f.Graph.CreateResponseWithoutFields = true;
        Assert(await f.Sync.GetQuotaAsync("doc-a", default) == 3, "quota bootstrap supports a Graph 201 without expanded fields");
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows["d"].Count == 1 && f.Graph.Rows["l"].Count == 0, "three unused codes create one annual row and no licenses");
        Assert(f.Graph.Annual(2026).Fields["LicenzeConsumate"].GetInt32() == 0, "bootstrap consumption is zero");
        Assert(f.Graph.Rows["a"].Count == 3, "unused codes can produce deduplicated discovery logs");
        var request = f.Request(f.CodeA, "doc-a");
        var signed = f.Activate(request, 3, 2026)!;
        var payload = Decode(signed);
        Assert(Crypto.GetExpiration(payload) == DateTimeOffset.Parse(payload.IssuedAt).AddDays(30), "signed validity remains thirty days");
        await f.Sync.SynchronizeAsync(default);
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows["l"].Count == 1 && f.Graph.Rows["a"].Count == 4, "retries do not duplicate activated rows or logs");
        var license = f.Graph.Rows["l"].Single();
        Assert(license.Fields["CodiceAttivazione"].GetString() == f.CodeA, "plaintext operator code is retained when available");
        foreach (var field in new Dictionary<string, string> { ["Nome"] = "Mario", ["Cognome"] = "Rossi", ["CodiceUtente"] = "USR-42", ["NomeMacchina"] = "PC-MARIO", ["Piattaforma"] = "Windows" })
            Assert(license.Fields[field.Key].GetString() == field.Value, "profile projection: " + field.Key);
        Assert(license.Fields["Stato"].GetString() == "Attiva", "signed activation is administratively active");
        Assert(DateTimeOffset.Parse(license.Fields["DataFine"].GetString()!) == new DateTimeOffset(2100, 12, 31, 0, 0, 0, TimeSpan.Zero), "administrative expiry is 31 December 2100 UTC");
        Assert(license.Fields["DataInizio"].GetString() == payload.IssuedAt, "signed issue time is retained");
        Assert(DateTimeOffset.Parse(license.Fields["ScadenzaTecnica"].GetString()!) == Crypto.GetExpiration(payload), "optional technical expiry stores actual signed expiry");
        Assert(!license.Fields.ContainsKey("DistribuzioneLookupId"), "license writes do not depend on a legacy lookup");
        Assert(f.Graph.Annual(2026).Fields["LicenzeConsumate"].GetInt32() == 1, "first activation consumes one annual seat");
        f.Graph.Set("l", license.Id, new Dictionary<string, object?> { ["Note"] = "Nota operatore", ["Stato"] = "Scaduta", ["Nome"] = "Corretto" });
        f.Graph.Set("d", f.Graph.Annual(2026).Id, new Dictionary<string, object?> { ["LicenzeTotali"] = 8 });
        f.Activate(f.Request(f.CodeB, "doc-b"), 8, 2026);
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows["d"].Count == 1 && f.Graph.Annual(2026).Fields["LicenzeConsumate"].GetInt32() == 2, "different documents consume the same annual row");
        Assert(await f.Sync.GetQuotaAsync("doc-b", default) == 8, "operator quota changes survive bootstrap configuration");
        license = f.Graph.Rows["l"].Single(row => row.Id == license.Id);
        Assert(license.Fields["Stato"].GetString() == "Scaduta" && license.Fields["Note"].GetString() == "Nota operatore" && license.Fields["Nome"].GetString() == "Corretto", "operator edits survive sync");
        Assert(!f.Graph.Annual(2026).Fields.ContainsKey("LicenzeDisponibili"), "calculated available count is never written");
        Assert(f.Journal.Pending().Count == 0, "events are acknowledged after projection");
        f.Journal.Record(new(Guid.NewGuid().ToString("N"), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), "ErroreTecnico", "Successo", "Avvio servizio"));
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows["d"].Count == 1 && f.Journal.Pending().Count == 0, "calendar rollover does not change the configured quota year for generic events");
        Assert(!f.Graph.Rows["a"].Single(row => row.Fields["Title"].GetString() == "Avvio servizio").Fields.ContainsKey("DistribuzioneLookupId"), "generic events do not invent an annual allocation");
        f.Graph.Set("d", f.Graph.Annual(2026).Id, new Dictionary<string, object?> { ["LicenzeTotali"] = 8.0m });
        Assert(await f.Sync.GetQuotaAsync("doc-a", default) == 8, "integral decimal quotas are accepted");
        f.Graph.Set("d", f.Graph.Annual(2026).Id, new Dictionary<string, object?> { ["LicenzeTotali"] = 1.5m });
        await ThrowsAsync(() => f.Sync.GetQuotaAsync("doc-a", default), "fractional quotas must be rejected");
    }

    // Catches counting old activations against the configured year or inventing old-year totals.
    private static async Task HistoricalYears()
    {
        using var f = new Fixture();
        using (var oldStore = new ActivationStore(Path.Combine(f.Root, "doc-a"), new FixedClock(new(2025, 12, 31, 12, 0, 0, TimeSpan.Zero))))
            oldStore.Activate(f.Request(f.CodeA, "doc-a"), profile: new("Mario", "Rossi", "USR-42", "PC-MARIO", "Windows"));
        await ThrowsAsync(() => f.Sync.SynchronizeAsync(default), "missing historical row requires explicit provisioning", "2025");
        Assert(f.Graph.Rows["d"].All(row => row.Fields["Anno"].GetInt32() != 2025), "historical totals are never invented");
        f.Graph.AddAnnual(2025, 12);
        f.Activate(f.Request(f.CodeB, "doc-b"), 3, 2026);
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows["l"].Count == 2, "historical signed activations remain projected");
        Assert(f.Graph.Annual(2025).Fields["LicenzeConsumate"].GetInt32() == 1 && f.Graph.Annual(2026).Fields["LicenzeConsumate"].GetInt32() == 1, "activations count only in their effective quota year");
        var oldLicense = f.Graph.Rows["l"].Single(row => row.Fields["CodiceAttivazione"].GetString() == f.CodeA);
        Assert(oldLicense.Fields["Stato"].GetString() == "Attiva", "technical expiration never automatically expires administrative status");
        var oldLog = f.Graph.Rows["a"].Single(row => row.Fields["LicenseId"].ToString() == oldLicense.Fields["LicenseId"].ToString());
        Assert(oldLog.Fields["DistribuzioneLookupId"].GetString() == f.Graph.Annual(2025).Id, "historical activation log uses its original annual row");
    }

    // Catches silently accepting the wrong tenant schema before an activation.
    private static async Task ConcurrentConsumptionRefresh()
    {
        using var f = new Fixture();
        await f.Sync.GetQuotaAsync("doc-a", default);
        f.Options.DocumentIds.Remove("doc-b");
        f.Activate(f.Request(f.CodeA, "doc-a"), 3, 2026);
        f.Graph.BeforeAnnualUpdate = id =>
        {
            f.Activate(f.Request(f.CodeB, "doc-b"), 3, 2026);
            f.Graph.Set("d", id, new Dictionary<string, object?> { ["LicenzeConsumate"] = 2 });
        };
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Annual(2026).Fields["LicenzeConsumate"].GetInt32() == 2, "a stale annual ETag refreshes all catalog documents before retrying consumption");
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows["l"].Count == 2, "existing activations in other catalog documents remain projected");
    }

    // Catches silently accepting the wrong tenant schema before an activation.
    private static async Task SchemaPreflight()
    {
        using var f = new Fixture();
        var original = f.Graph.Columns["l"].Single(c => c.Name == "CodiceUtente");
        f.Graph.Columns["l"].Remove(original);
        await ThrowsAsync(() => f.Sync.GetQuotaAsync("doc-a", default), "missing user code has an actionable preflight error", "CodiceUtente");
        Assert(f.Graph.Rows.Values.All(rows => rows.Count == 0), "schema failures do not write rows");
        f.Graph.Columns["l"].Add(original);
        f.Graph.Columns["l"].RemoveAll(c => c.Name is "ScadenzaTecnica" or "CodiceAttivazione");
        f.Graph.Columns["a"].RemoveAll(c => c.Name == "Distribuzione");
        int consumed = f.Graph.Columns["d"].FindIndex(c => c.Name == "LicenzeConsumate");
        f.Graph.Columns["d"][consumed] = new("ConsumedInternal", "LicenzeImpegnate", "number", false, false, false);
        f.Activate(f.Request(f.CodeA, "doc-a"), 3, 2026);
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Annual(2026).Fields["ConsumedInternal"].GetInt32() == 1, "renamed consumed column resolves its internal name");
        Assert(!f.Graph.Rows["l"][0].Fields.ContainsKey("ScadenzaTecnica"), "technical expiry column is optional");
        int year = f.Graph.Columns["d"].FindIndex(c => c.Name == "Anno");
        f.Graph.Columns["d"][year] = f.Graph.Columns["d"][year] with { Indexed = false };
        await ThrowsAsync(() => f.Sync.ValidateAsync(default), "year must be unique and indexed", "Anno");
        f.Graph.Columns["d"][year] = f.Graph.Columns["d"][year] with { Indexed = true };
        f.Graph.Columns["a"].Add(new("Distribuzione", "Distribuzione", "lookup", false, false, false, "d") { LookupAllowsMultiple = true });
        await ThrowsAsync(() => f.Sync.ValidateAsync(default), "present log lookup must be single-valued");
        f.Graph.Columns["a"].RemoveAll(c => c.Name == "Distribuzione");
        f.Options.DocumentIds.Add("missing-document");
        await ThrowsAsync(() => f.Sync.ValidateAsync(default), "configured documents must exist locally");
    }

    private static async Task InternalNamesWinOverDisplayAliases()
    {
        using var f = new Fixture();
        foreach (var columns in f.Graph.Columns.Values)
        {
            // Real SharePoint lists return all three with displayName = Title.
            columns.Add(new("LinkTitle", "Title", "unknown", true, false, false));
            columns.Add(new("LinkTitleNoMenu", "Title", "unknown", true, false, false));
        }
        f.Graph.Columns["l"].Add(new("OtherUserCode", "CodiceUtente", "text", false, false, false));
        await f.Sync.ValidateAsync(default);
        f.Activate(f.Request(f.CodeA, "doc-a"), 3, 2026);
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows.Values.All(rows => rows.All(row => row.Fields.ContainsKey("Title")
            && !row.Fields.ContainsKey("LinkTitle") && !row.Fields.ContainsKey("LinkTitleNoMenu"))),
            "schema and writes use canonical internal names despite built-in display aliases");
        Assert(f.Graph.Rows["l"].Single().Fields.ContainsKey("CodiceUtente")
            && !f.Graph.Rows["l"].Single().Fields.ContainsKey("OtherUserCode"), "internal profile name takes precedence");
        int titleIndex = f.Graph.Columns["d"].FindIndex(c => c.Name == "Title");
        f.Graph.Columns["d"][titleIndex] = f.Graph.Columns["d"][titleIndex] with { ReadOnly = true };
        await ThrowsAsync(() => f.Sync.ValidateAsync(default), "a read-only canonical column must still be rejected", "Title");
        f.Graph.Columns["d"].RemoveAll(c => c.Name == "Title");
        await ThrowsAsync(() => f.Sync.ValidateAsync(default), "ambiguous display-only fallback must still fail safely", "Title");
    }

    // Catches acknowledging failed writes, losing operator edits on 412 and duplicating conflict retries.
    private static async Task ConflictAndOutageRecovery()
    {
        using var f = new Fixture();
        var request = f.Request(f.CodeA, "doc-a");
        var signed = f.Activate(request, 3, 2026)!;
        f.Graph.ConflictOnLicense = true;
        await ThrowsAsync(() => f.Sync.SynchronizeAsync(default), "unresolved unique conflicts remain pending");
        Assert(f.Graph.Rows["a"].Count == 0, "license conflicts are not falsely acknowledged");
        f.Graph.ConflictOnLicense = false;
        await f.Sync.SynchronizeAsync(default);
        var license = f.Graph.Rows["l"].Single();
        f.Graph.Set("l", license.Id, new Dictionary<string, object?> { ["DataFine"] = "2026-01-01T00:00:00Z" });
        f.Graph.ReplaceOnPatch = true;
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Graph.Rows["l"].Single().Fields["Stato"].GetString() == "Sostituita", "operator replacement survives a 412 retry");
        var coordinator = new ActivationCoordinator(f.Catalog, f.Sync);
        f.Graph.Fail = true;
        Assert(await coordinator.ActivateAsync(request, default) == signed, "existing signed response survives SharePoint outage");
        Assert(await coordinator.ActivateAsync(request with { DocumentId = "unknown" }, default) is null, "unknown document rejected before remote lookup");
        Assert(await coordinator.ActivateAsync(request with { Code = new string('0', 32) }, default) is null, "unknown code rejected before remote lookup");
        f.Journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "Attivazione", "Rifiutato", "Rifiuto", "doc-a"));
        try { await f.Sync.SynchronizeAsync(default); throw new Exception("remote outage ignored"); } catch (HttpRequestException) { }
        Assert(f.Journal.Pending().Count == 1, "outage leaves durable events pending");
        f.Graph.Fail = false;
        f.Graph.ConflictOnLogOnce = true;
        await f.Sync.SynchronizeAsync(default);
        await f.Sync.SynchronizeAsync(default);
        Assert(f.Journal.Pending().Count == 0 && f.Graph.Rows["l"].Count == 1 && f.Graph.Rows["a"].Count == 5, "log conflicts verify identity before acknowledgement");
        f.Graph.AddAnnual(2026, 9);
        await ThrowsAsync(() => f.Sync.GetQuotaAsync("doc-a", default), "duplicate annual rows fail safely", "2026");
    }

    private static LicensePayload Decode(SignedLicense signed) => JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(signed.Payload))!;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync(Func<Task> action, string message, string? expectedError = null)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (expectedError is null || error.Message.Contains(expectedError, StringComparison.Ordinal)) { return; }
        throw new Exception(message);
    }
    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider { public override DateTimeOffset GetUtcNow() => instant; }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pdf-annual-sync-" + Guid.NewGuid().ToString("N"));
        readonly RSA authority = RSA.Create(2048), device = RSA.Create(2048);
        public string CodeA { get; }
        public string CodeB { get; }
        public ActivationCatalog Catalog { get; }
        public AuditJournal Journal { get; }
        public MemoryGraph Graph { get; } = new();
        public SharePointOptions Options { get; } = new() { Enabled = true, DistributionListId = "d", LicenseListId = "l", LogListId = "a", QuotaYear = 2026, InitialLicenseTotal = 3, DocumentIds = ["doc-a", "doc-b"] };
        public SharePointSynchronizer Sync { get; }
        public Fixture(TimeProvider? clock = null)
        {
            Directory.CreateDirectory(Root);
            CodeA = ActivationStore.Create(Path.Combine(Root, "doc-a"), "doc-a", new byte[32], authority);
            using (var store = new ActivationStore(Path.Combine(Root, "doc-a"))) store.IssueCode();
            CodeB = ActivationStore.Create(Path.Combine(Root, "doc-b"), "doc-b", new byte[32], authority);
            Catalog = new(Root, clock);
            Journal = new(Path.Combine(Root, "audit"));
            Sync = new(Graph, Options, Catalog, Journal, NullLogger<SharePointSynchronizer>.Instance);
        }
        public ActivationRequest Request(string code, string document) => new(code, document, Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), "Mario", "Rossi", "USR-42", "PC-MARIO", "Windows");
        public SignedLicense? Activate(ActivationRequest request, int total, int year) => Catalog.Activate(request, total, year, new("Mario", "Rossi", "USR-42", "PC-MARIO", "Windows"));
        public void Dispose() { authority.Dispose(); device.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class MemoryGraph : IGraphLists
    {
        public bool Fail, ReplaceOnPatch, ConflictOnLicense, ConflictOnLogOnce, CreateResponseWithoutFields;
        public Action<string>? BeforeAnnualUpdate;
        public Dictionary<string, List<GraphItem>> Rows = new() { ["d"] = [], ["l"] = [], ["a"] = [] };
        public Dictionary<string, List<GraphColumn>> Columns = new()
        {
            ["d"] = Cols("Title:text", "Anno:number:unique", "LicenzeTotali:number", "LicenzeConsumate:number", "LicenzeDisponibili:calculated", "Note:text"),
            ["l"] = Cols("Title:text", "Nome:text", "Cognome:text", "CodiceUtente:text", "NomeMacchina:text", "CodiceHash:text:unique", "CodiceAttivazione:text", "LicenseId:text", "Stato:choice", "DeviceKeyHash:text", "DataInizio:dateTime", "DataFine:dateTime", "ScadenzaTecnica:dateTime", "Piattaforma:choice", "Note:text"),
            ["a"] = Cols("Title:text", "EventId:text:unique", "DataEvento:dateTime", "TipoEvento:choice", "Esito:choice", "Distribuzione:lookup", "LicenseId:text", "RequestId:text", "OperatoreEmail:text", "Dettagli:text")
        };
        private static List<GraphColumn> Cols(params string[] fields) => fields.Select(f => f.Split(':')).Select(f => new GraphColumn(f[0], f[0], f[1], f[1] == "calculated", f.Length > 2, f.Length > 2, f[1] == "lookup" ? "d" : null)).ToList();
        private static GraphItem Copy(GraphItem row) => row with { Fields = new(row.Fields) };
        private void Check() { if (Fail) throw new HttpRequestException(); }
        public Task<IReadOnlyList<GraphColumn>> GetColumnsAsync(string list, CancellationToken ct) { Check(); return Task.FromResult<IReadOnlyList<GraphColumn>>(Columns[list].ToArray()); }
        public Task<IReadOnlyList<GraphItem>> GetItemsAsync(string list, CancellationToken ct) { Check(); return Task.FromResult<IReadOnlyList<GraphItem>>(Rows[list].Select(Copy).ToArray()); }
        public Task<GraphItem?> FindAsync(string list, string field, string value, CancellationToken ct)
        { Check(); var row = Rows[list].SingleOrDefault(r => r.Fields.TryGetValue(field, out var p) && p.ToString() == value); return Task.FromResult(row is null ? null : Copy(row)); }
        public Task<GraphItem> CreateAsync(string list, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
        {
            Check();
            if (list == "l" && ConflictOnLicense) throw new GraphApiException(HttpStatusCode.Conflict, false);
            CheckFields(list, fields);
            var row = new GraphItem((Rows[list].Count + 1).ToString(), Guid.NewGuid().ToString(), fields.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value)));
            Rows[list].Add(row);
            if (list == "a" && ConflictOnLogOnce) { ConflictOnLogOnce = false; throw new GraphApiException(HttpStatusCode.Conflict, false); }
            return Task.FromResult(CreateResponseWithoutFields ? new GraphItem(row.Id, null, []) : Copy(row));
        }
        public Task UpdateAsync(string list, string id, IReadOnlyDictionary<string, object?> fields, string? etag, CancellationToken ct)
        {
            Check(); CheckFields(list, fields);
            if (list == "d" && BeforeAnnualUpdate is { } concurrent) { BeforeAnnualUpdate = null; concurrent(id); }
            if (ReplaceOnPatch && list == "l") { ReplaceOnPatch = false; Set(list, id, new Dictionary<string, object?> { ["Stato"] = "Sostituita" }); throw new GraphApiException(HttpStatusCode.PreconditionFailed, false); }
            if (Rows[list].Single(r => r.Id == id).ETag != etag) throw new GraphApiException(HttpStatusCode.PreconditionFailed, false);
            Set(list, id, fields); return Task.CompletedTask;
        }
        private void CheckFields(string list, IReadOnlyDictionary<string, object?> fields)
        {
            foreach (string field in fields.Keys)
            {
                var column = Columns[list].SingleOrDefault(c => c.Name == field || c.Type == "lookup" && c.Name + "LookupId" == field);
                if (column is null || column.ReadOnly) throw new Exception("Invalid Graph field write: " + field);
            }
        }
        public void Set(string list, string id, IReadOnlyDictionary<string, object?> fields)
        {
            int index = Rows[list].FindIndex(r => r.Id == id);
            var values = new Dictionary<string, JsonElement>(Rows[list][index].Fields);
            foreach (var pair in fields) values[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
            Rows[list][index] = Rows[list][index] with { ETag = Guid.NewGuid().ToString(), Fields = values };
        }
        public void AddAnnual(int year, int total) => Rows["d"].Add(new((Rows["d"].Count + 1).ToString(), Guid.NewGuid().ToString(), new() { ["Title"] = JsonSerializer.SerializeToElement(year.ToString()), ["Anno"] = JsonSerializer.SerializeToElement(year), ["LicenzeTotali"] = JsonSerializer.SerializeToElement(total), ["LicenzeConsumate"] = JsonSerializer.SerializeToElement(0) }));
        public GraphItem Annual(int year) => Rows["d"].Single(row => row.Fields["Anno"].GetInt32() == year);
    }
}
