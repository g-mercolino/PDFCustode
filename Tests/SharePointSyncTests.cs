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
        string root = Path.Combine(Path.GetTempPath(), "pdf-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var authority = System.Security.Cryptography.RSA.Create(2048);
            using var device = System.Security.Cryptography.RSA.Create(2048);
            var code = ActivationStore.Create(root, "doc", new byte[32], authority);
            var catalog = new ActivationCatalog(root);
            var journal = new AuditJournal(Path.Combine(root, "audit"));
            var graph = new MemoryGraph { CreateResponseWithoutFields = true };
            var options = new SharePointOptions
            {
                Enabled = true, SiteId = "site", DistributionListId = "d", LicenseListId = "l", LogListId = "a",
                Distributions = [new DistributionMapping { DocumentId = "doc", DistribuzioneId = "D-1", Title = "Demo", Cliente = "Test", VersioneDocumento = "1", LicenzeTotali = 1 }]
            };
            var sync = new SharePointSynchronizer(graph, options, catalog, journal, NullLogger<SharePointSynchronizer>.Instance);
            Assert(await sync.GetQuotaAsync("doc", default) == 1, "first quota bootstrap supports Graph creation without expanded fields");
            graph.ConflictOnLicense = true;
            await ThrowsAsync(() => sync.SynchronizeAsync(default), "a conflict without a matching license must fail sync");
            Assert(graph.Rows["a"].Count == 0, "unresolved license conflict is not acknowledged as synced");
            graph.ConflictOnLicense = false;
            await sync.SynchronizeAsync(default);
            Assert(graph.Rows["d"].Count == 1 && graph.Rows["l"].Count == 1, "bootstrap rows");
            var entry = graph.Rows["l"][0];
            Assert(entry.Fields["CodiceAttivazione"].GetString() == code, "operator receives code, not only hash");
            Assert(await sync.GetQuotaAsync("doc", default) == 1, "quota read from SharePoint");
            graph.Rows["d"][0].Fields["LicenzeTotali"] = JsonSerializer.Deserialize<JsonElement>("1.0");
            Assert(await sync.GetQuotaAsync("doc", default) == 1, "SharePoint decimal JSON numbers support integral quotas");
            graph.Rows["d"][0].Fields["LicenzeTotali"] = JsonSerializer.Deserialize<JsonElement>("1.5");
            await ThrowsAsync(async () => { await sync.GetQuotaAsync("doc", default); }, "fractional quota rejected");
            graph.Rows["d"][0].Fields["LicenzeTotali"] = JsonSerializer.SerializeToElement(1);
            var request = new ActivationRequest(code, "doc", Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()));
            var signed = catalog.Activate(request, 1)!;
            var payload = Crypto.Verify(signed, Convert.ToBase64String(authority.ExportSubjectPublicKeyInfo()), "doc", request.DevicePublicKey);
            graph.Set("l", entry.Id, new Dictionary<string, object?> { ["NomeDestinatario"] = "Operatore", ["EmailDestinatario"] = "test@example.invalid", ["Stato"] = "Assegnata" });
            journal.Record(new AuditEvent(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "Attivazione", "Successo", "Attivazione riuscita", "doc", payload.LicenseId, "req-test", "LicenseIssued"));
            await sync.SynchronizeAsync(default);
            await sync.SynchronizeAsync(default);
            Assert(graph.Rows["l"].Count == 1 && graph.Rows["a"].Count == 3, "replay does not duplicate discovery, activation or request events");
            entry = graph.Rows["l"][0];
            Assert(entry.Fields["Stato"].GetString() == "Attiva", "activation reflected");
            Assert(entry.Fields["DataInizio"].GetString() == payload.IssuedAt && entry.Fields["DataFine"].GetString() == payload.ExpiresAt, "signed UTC timestamps preserved");
            Assert(entry.Fields["NomeDestinatario"].GetString() == "Operatore", "operator fields preserved");
            Assert(graph.Rows["d"][0].Fields["LicenzeConsumate"].GetInt32() == 1, "consumption is absolute, no repeated subtraction");
            Assert(!graph.Rows["d"][0].Fields.ContainsKey("LicenzeDisponibili"), "calculated column never written");
            Assert(journal.Pending().Count == 0, "ack only after upload");
            var coordinator = new ActivationCoordinator(catalog, sync);
            graph.Fail = true;
            Assert(await coordinator.ActivateAsync(request, default) == signed, "existing response survives SharePoint outage");
            Assert(await coordinator.ActivateAsync(request with { DocumentId = "unknown" }, default) is null, "unknown document rejected before Graph lookup");
            Assert(await coordinator.ActivateAsync(request with { Code = new string('0', 32) }, default) is null, "invalid code rejected before Graph lookup");
            graph.Fail = false;
            graph.ReplaceOnPatch = true;
            // Force a patch so the fake can simulate an operator edit racing with the ETag.
            graph.Set("l", entry.Id, new Dictionary<string, object?> { ["Stato"] = "Assegnata" });
            await sync.SynchronizeAsync(default);
            Assert(graph.Rows["l"][0].Fields["Stato"].GetString() == "Sostituita", "operator replacement survives a 412 retry");
            var audit = new AuditEvent(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "Attivazione", "Rifiutato", "Rifiuto", "doc");
            journal.Record(audit);
            graph.Fail = true;
            try { await sync.SynchronizeAsync(default); throw new Exception("outage accepted"); }
            catch (HttpRequestException) { }
            Assert(journal.Pending().Count == 1, "outage keeps durable event");
            graph.Fail = false;
            graph.Set("d", graph.Rows["d"][0].Id, new Dictionary<string, object?> { ["LicenzeTotali"] = 5 });
            await sync.SynchronizeAsync(default);
            Assert(await sync.GetQuotaAsync("doc", default) == 5, "operator total is not overwritten by bootstrap config");
            graph.Set("d", graph.Rows["d"][0].Id, new Dictionary<string, object?> { ["LicenzeConsumate"] = 0 });
            graph.ChangeDocumentOnPatch = true;
            await ThrowsAsync(() => sync.SynchronizeAsync(default), "a concurrent document reassignment fails safely");
            Assert(graph.Rows["d"][0].Fields["DocumentId"].GetString() == "other-document", "concurrent document assignment is not overwritten");
            graph.Set("d", graph.Rows["d"][0].Id, new Dictionary<string, object?> { ["DocumentId"] = "doc" });
            var stateColumn = graph.Columns["l"].FindIndex(c => c.Name == "Stato");
            var originalStateColumn = graph.Columns["l"][stateColumn];
            graph.Columns["l"][stateColumn] = originalStateColumn with { Choices = ["Disponibile"] };
            await ThrowsAsync(() => sync.ValidateAsync(default), "missing Choice values rejected before writes");
            graph.Columns["l"][stateColumn] = originalStateColumn;
            var lookupColumn = graph.Columns["l"].FindIndex(c => c.Name == "Distribuzione");
            var originalLookupColumn = graph.Columns["l"][lookupColumn];
            graph.Columns["l"][lookupColumn] = originalLookupColumn with { LookupAllowsMultiple = true };
            await ThrowsAsync(() => sync.ValidateAsync(default), "multi-lookup rejected before writes");
            graph.Columns["l"][lookupColumn] = originalLookupColumn;
            options.Distributions[0].DocumentId = "missing-local-document";
            await ThrowsAsync(() => sync.ValidateAsync(default), "mapping checked before first write");
            options.Distributions[0].DocumentId = "doc";
            graph.Columns["l"].RemoveAll(c => c.Name == "DataFine");
            await ThrowsAsync(() => sync.ValidateAsync(default), "missing schema rejected");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("SharePoint projection tests passed.");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }

    private sealed class MemoryGraph : IGraphLists
    {
        public bool Fail;
        public bool ReplaceOnPatch;
        public bool ConflictOnLicense;
        public bool ChangeDocumentOnPatch;
        public bool CreateResponseWithoutFields;
        public Dictionary<string, List<GraphItem>> Rows = new() { ["d"] = [], ["l"] = [], ["a"] = [] };
        public Dictionary<string, List<GraphColumn>> Columns = new()
        {
            ["d"] = Cols("Title:text", "DistribuzioneId:text:unique", "Cliente:text", "DocumentId:text", "VersioneDocumento:text", "LicenzeTotali:number", "LicenzeConsumate:number", "LicenzeDisponibili:calculated"),
            ["l"] = Cols("Title:text", "Distribuzione:lookup", "CodiceHash:text:unique", "CodiceAttivazione:text", "LicenseId:text", "Stato:choice", "NomeDestinatario:text", "EmailDestinatario:text", "DeviceKeyHash:text", "DataInizio:dateTime", "DataFine:dateTime", "Piattaforma:choice", "Note:text"),
            ["a"] = Cols("Title:text", "EventId:text:unique", "DataEvento:dateTime", "TipoEvento:choice", "Esito:choice", "Distribuzione:lookup", "LicenseId:text", "RequestId:text", "OperatoreEmail:text", "Dettagli:text")
        };
        private static List<GraphColumn> Cols(params string[] fields) => fields.Select(f => f.Split(':')).Select(f => new GraphColumn(f[0], f[0], f[1], f[1] == "calculated", f.Length > 2, f.Length > 2, f[1] == "lookup" ? "d" : null)).ToList();
        public Task<IReadOnlyList<GraphColumn>> GetColumnsAsync(string list, CancellationToken ct) { if (Fail) throw new HttpRequestException(); return Task.FromResult<IReadOnlyList<GraphColumn>>(Columns[list]); }
        public Task<IReadOnlyList<GraphItem>> GetItemsAsync(string list, CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphItem>>(Rows[list].ToArray());
        public Task<GraphItem?> FindAsync(string list, string field, string value, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException();
            return Task.FromResult(Rows[list].SingleOrDefault(r => r.Fields.TryGetValue(field, out var p) && p.ToString() == value));
        }
        public Task<GraphItem> CreateAsync(string list, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
        {
            if (list == "l" && ConflictOnLicense) throw new GraphApiException(System.Net.HttpStatusCode.Conflict, false);
            var row = new GraphItem((Rows[list].Count + 1).ToString(), "etag", fields.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value)));
            Rows[list].Add(row);
            return Task.FromResult(CreateResponseWithoutFields ? new GraphItem(row.Id, null, []) : row);
        }
        public Task UpdateAsync(string list, string id, IReadOnlyDictionary<string, object?> fields, string? etag, CancellationToken ct)
        {
            if (ChangeDocumentOnPatch && list == "d")
            {
                ChangeDocumentOnPatch = false;
                Set(list, id, new Dictionary<string, object?> { ["DocumentId"] = "other-document" });
                throw new GraphApiException(System.Net.HttpStatusCode.PreconditionFailed, false);
            }
            if (ReplaceOnPatch && list == "l")
            {
                ReplaceOnPatch = false;
                Set(list, id, new Dictionary<string, object?> { ["Stato"] = "Sostituita" });
                throw new GraphApiException(System.Net.HttpStatusCode.PreconditionFailed, false);
            }
            Set(list, id, fields); return Task.CompletedTask;
        }
        public void Set(string list, string id, IReadOnlyDictionary<string, object?> fields)
        {
            var row = Rows[list].Single(r => r.Id == id);
            foreach (var pair in fields) row.Fields[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
        }
    }
}
