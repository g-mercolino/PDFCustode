using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfCustode.Core;
using PdfCustode.Server.Audit;

namespace PdfCustode.Server.SharePoint;

/// <summary>Projects local signed activations into the operator-owned SharePoint lists.</summary>
public sealed class SharePointSynchronizer(
    IGraphLists graph, SharePointOptions options, ActivationCatalog catalog,
    AuditJournal journal, ILogger<SharePointSynchronizer> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, string>> fieldNames = [];

    public async Task ValidateAsync(CancellationToken ct)
    {
        await ValidateList(options.DistributionListId,
            ["Title:text", "DistribuzioneId:text", "Cliente:text", "DocumentId:text", "VersioneDocumento:text", "LicenzeTotali:number", "LicenzeConsumate:number"], "DistribuzioneId", ct);
        await ValidateList(options.LicenseListId,
            ["Title:text", "Distribuzione:lookup", "CodiceHash:text", "CodiceAttivazione:text", "LicenseId:text", "Stato:choice", "DeviceKeyHash:text", "DataInizio:dateTime", "DataFine:dateTime"], "CodiceHash", ct);
        await ValidateList(options.LogListId,
            ["Title:text", "EventId:text", "DataEvento:dateTime", "TipoEvento:choice", "Esito:choice", "Distribuzione:lookup", "LicenseId:text", "RequestId:text", "Dettagli:text"], "EventId", ct);
        var snapshots = catalog.GetSnapshots();
        foreach (var snapshot in snapshots) _ = GetMapping(snapshot.DocumentId);
        if (options.Distributions.Any(d => !snapshots.Any(s => s.DocumentId == d.DocumentId)))
            throw new InvalidOperationException("DocumentId configurato ma assente dall'archivio amministrativo locale.");
    }

    private async Task ValidateList(string list, string[] expected, string unique, CancellationToken ct)
    {
        var columns = await graph.GetColumnsAsync(list, ct);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in expected)
        {
            var parts = definition.Split(':');
            var matches = columns.Where(c => string.Equals(c.Name, parts[0], StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.DisplayName, parts[0], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"Colonna SharePoint mancante o ambigua: {parts[0]}.");
            var column = matches[0];
            if (column.Type != parts[1] || column.ReadOnly)
                throw new InvalidOperationException($"Tipo colonna SharePoint errato: {parts[0]}, richiesto {parts[1]} modificabile.");
            if (parts[0] == unique && (!column.EnforceUniqueValues || !column.Indexed))
                throw new InvalidOperationException($"Abilitare valori univoci e indice su {parts[0]} per consentire tentativi ripetuti senza duplicati.");
            if (parts[1] == "lookup" && column.LookupListId is not null
                && !SameList(column.LookupListId, options.DistributionListId))
                throw new InvalidOperationException("La colonna Distribuzione deve puntare a PDF_Distribuzioni.");
            if (parts[1] == "lookup" && column.LookupAllowsMultiple)
                throw new InvalidOperationException("La colonna Distribuzione deve essere una lookup singola.");
            string[]? requiredChoices = parts[0] switch
            {
                "Stato" => ["Disponibile", "Assegnata", "Attiva", "Scaduta", "Sostituita"],
                "Esito" => ["Successo", "Rifiutato", "Errore"],
                "TipoEvento" => ["CodiceGenerato", "CodiceAssegnato", "Attivazione", "Rinnovo", "Sostituzione", "ErroreTecnico"],
                _ => null
            };
            if (column.Choices is not null && requiredChoices is not null && requiredChoices.Any(c => !column.Choices.Contains(c)))
                throw new InvalidOperationException($"Valori Choice mancanti nella colonna {parts[0]}.");
            mapping.Add(parts[0], column.Name);
        }
        fieldNames[list] = mapping;
    }

    private static bool SameList(string left, string right) => string.Equals(left.Trim('{', '}'), right.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);
    private string Field(string list, string name) => fieldNames[list][name];
    private Dictionary<string, object?> Fields(string list, Dictionary<string, object?> fields) =>
        fields.ToDictionary(p => p.Key.EndsWith("LookupId", StringComparison.Ordinal)
            ? Field(list, p.Key[..^8]) + "LookupId" : Field(list, p.Key), p => p.Value);
    private string? Text(GraphItem row, string list, string name) =>
        row.Fields.TryGetValue(Field(list, name), out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    public async Task<int> GetQuotaAsync(string documentId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (fieldNames.Count != 3) await ValidateAsync(ct);
            var mapping = GetMapping(documentId);
            var distribution = await EnsureDistribution(mapping, ct);
            if (!distribution.Fields.TryGetValue(Field(options.DistributionListId, "LicenzeTotali"), out var raw)
                || raw.ValueKind != JsonValueKind.Number || !raw.TryGetDecimal(out var total)
                || total < 0 || total > int.MaxValue || decimal.Truncate(total) != total)
                throw new InvalidOperationException("LicenzeTotali deve essere un numero intero non negativo.");
            return (int)total;
        }
        finally { gate.Release(); }
    }

    private DistributionMapping GetMapping(string documentId) =>
        options.Distributions.SingleOrDefault(d => d.DocumentId == documentId)
        ?? throw new InvalidOperationException("Documento privo di una distribuzione SharePoint configurata.");

    public async Task SynchronizeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await ValidateAsync(ct);
            var distributionRows = new Dictionary<string, GraphItem>();
            foreach (var snapshot in catalog.GetSnapshots())
            {
                var mapping = GetMapping(snapshot.DocumentId);
                var distribution = await EnsureDistribution(mapping, ct);
                distributionRows[snapshot.DocumentId] = distribution;
                foreach (var code in snapshot.Codes)
                {
                    ct.ThrowIfCancellationRequested();
                    await SyncLicense(code, snapshot, distribution, ct);
                    var eventId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("code-discovered:" + snapshot.DocumentId + ":" + code.CodeHash)))[..32].ToLowerInvariant();
                    journal.Record(new(eventId, DateTimeOffset.UtcNow, "CodiceGenerato", "Successo", "Codice rilevato nel registro locale", snapshot.DocumentId,
                        Details: JsonSerializer.Serialize(new { reason = "ExistingCodeDiscovered" })));
                    if (code.License is not null)
                    {
                        var payload = JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(code.License.Payload))!;
                        string activationId = StableId("activated:" + payload.LicenseId);
                        journal.Record(new(activationId, DateTimeOffset.Parse(payload.IssuedAt), "Attivazione", "Successo",
                            "Attivazione persistita", snapshot.DocumentId, payload.LicenseId,
                            Details: JsonSerializer.Serialize(new { reason = "PersistedLicense" })));
                        var expiration = Crypto.GetExpiration(payload);
                        if (DateTimeOffset.UtcNow >= expiration)
                            journal.Record(new(StableId("expired:" + payload.LicenseId), expiration, "Attivazione", "Successo",
                                "Scadenza licenza", snapshot.DocumentId, payload.LicenseId,
                                Details: JsonSerializer.Serialize(new { reason = "LicenseExpired" })));
                    }
                }
                // Re-read under the local lock after projecting licenses. Never decrement relatively.
                var latest = catalog.GetSnapshots().Single(s => s.DocumentId == snapshot.DocumentId);
                await Patch(options.DistributionListId, "DistribuzioneId", mapping.DistribuzioneId,
                    new() { ["LicenzeConsumate"] = latest.Codes.Count(c => c.License is not null) }, ct);
            }
            foreach (var entry in journal.Pending().Take(200))
            {
                ct.ThrowIfCancellationRequested();
                var fields = new Dictionary<string, object?>
                {
                    ["Title"] = entry.Title, ["EventId"] = entry.EventId, ["DataEvento"] = entry.DataEvento.ToUniversalTime().ToString("O"),
                    ["TipoEvento"] = entry.TipoEvento, ["Esito"] = entry.Esito, ["LicenseId"] = entry.LicenseId,
                    ["RequestId"] = entry.RequestId, ["Dettagli"] = entry.Details
                };
                if (entry.DocumentId is not null && distributionRows.TryGetValue(entry.DocumentId, out var distribution))
                    fields["DistribuzioneLookupId"] = distribution.Id;
                if (await graph.FindAsync(options.LogListId, Field(options.LogListId, "EventId"), entry.EventId, ct) is null)
                {
                    try { await graph.CreateAsync(options.LogListId, Fields(options.LogListId, fields), ct); }
                    catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                    {
                        if (await graph.FindAsync(options.LogListId, Field(options.LogListId, "EventId"), entry.EventId, ct) is null) throw;
                    }
                }
                journal.Acknowledge(entry.EventId);
            }
            logger.LogInformation("SharePoint sync completed; configured distributions {Count}", distributionRows.Count);
        }
        finally { gate.Release(); }
    }

    private static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();

    private async Task<GraphItem> EnsureDistribution(DistributionMapping mapping, CancellationToken ct)
    {
        var list = options.DistributionListId;
        var row = await graph.FindAsync(list, Field(list, "DistribuzioneId"), mapping.DistribuzioneId, ct);
        if (row is null)
        {
            try
            {
                row = await graph.CreateAsync(list, Fields(list, new()
                {
                    ["Title"] = mapping.Title, ["DistribuzioneId"] = mapping.DistribuzioneId,
                    ["Cliente"] = mapping.Cliente, ["DocumentId"] = mapping.DocumentId,
                    ["VersioneDocumento"] = mapping.VersioneDocumento,
                    ["LicenzeTotali"] = mapping.LicenzeTotali, ["LicenzeConsumate"] = 0
                }), ct);
            }
            catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            { row = await graph.FindAsync(list, Field(list, "DistribuzioneId"), mapping.DistribuzioneId, ct) ?? throw new InvalidOperationException("Distribuzione concorrente non recuperabile."); }
            // A Graph 201 response need not expand the fields relationship.
            row = await graph.FindAsync(list, Field(list, "DistribuzioneId"), mapping.DistribuzioneId, ct)
                ?? throw new InvalidOperationException("Distribuzione creata ma non ancora leggibile. Riprovare la sincronizzazione.");
        }
        var document = Text(row, list, "DocumentId");
        if (!string.IsNullOrEmpty(document) && document != mapping.DocumentId)
            throw new InvalidOperationException("DistribuzioneId già associato a un altro documento.");
        if (string.IsNullOrEmpty(document))
        {
            await Patch(list, "DistribuzioneId", mapping.DistribuzioneId, new() { ["DocumentId"] = mapping.DocumentId }, ct);
            row = await graph.FindAsync(list, Field(list, "DistribuzioneId"), mapping.DistribuzioneId, ct)
                ?? throw new InvalidOperationException("Distribuzione rimossa dopo l'aggiornamento.");
        }
        return row;
    }

    private async Task SyncLicense(ActivationSnapshot code, DocumentSnapshot document, GraphItem distribution, CancellationToken ct)
    {
        string list = options.LicenseListId;
        var existing = await graph.FindAsync(list, Field(list, "CodiceHash"), code.CodeHash, ct);
        string lookup = Field(list, "Distribuzione") + "LookupId";
        if (existing?.Fields.TryGetValue(lookup, out var assigned) == true && assigned.ToString() != distribution.Id)
            throw new InvalidOperationException("CodiceHash già associato a una distribuzione differente.");
        var values = new Dictionary<string, object?> { ["CodiceHash"] = code.CodeHash, ["DistribuzioneLookupId"] = distribution.Id };
        if (existing is null) { values["Title"] = "LIC-" + code.CodeHash[..12]; values["Stato"] = "Disponibile"; }
        if (code.ActivationCode is not null) values["CodiceAttivazione"] = code.ActivationCode;
        if (code.License is not null)
        {
            var signed = JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(code.License.Payload))
                ?? throw new InvalidDataException("Licenza locale non valida.");
            values["LicenseId"] = signed.LicenseId;
            values["DeviceKeyHash"] = signed.DeviceKeyHash;
            values["DataInizio"] = signed.IssuedAt;
            values["DataFine"] = Crypto.GetExpiration(signed).ToUniversalTime().ToString("O");
            if (existing is null || Text(existing, list, "Stato") != "Sostituita")
                values["Stato"] = DateTimeOffset.UtcNow >= Crypto.GetExpiration(signed) ? "Scaduta" : "Attiva";
        }
        if (existing is null)
        {
            try { await graph.CreateAsync(list, Fields(list, values), ct); }
            catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                var concurrent = await graph.FindAsync(list, Field(list, "CodiceHash"), code.CodeHash, ct)
                    ?? throw new InvalidOperationException("Conflitto SharePoint senza una riga licenza recuperabile.");
                if (!concurrent.Fields.TryGetValue(lookup, out var reference) || reference.ToString() != distribution.Id)
                    throw new InvalidOperationException("Licenza concorrente associata a una distribuzione differente.");
                values.Remove("Title");
                if (code.License is null) values.Remove("Stato");
                await Patch(list, "CodiceHash", code.CodeHash, values, ct);
            }
        }
        else await Patch(list, "CodiceHash", code.CodeHash, values, ct);
    }

    private async Task Patch(string list, string key, string value, Dictionary<string, object?> fields, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var current = await graph.FindAsync(list, Field(list, key), value, ct)
                ?? throw new InvalidOperationException("Riga SharePoint rimossa durante la sincronizzazione.");
            if (string.IsNullOrEmpty(current.ETag)) throw new InvalidOperationException("ETag SharePoint assente.");
            var translated = Fields(list, fields);
            if (list == options.DistributionListId)
            {
                var expectedDocument = options.Distributions.Single(d => d.DistribuzioneId == value).DocumentId;
                var currentDocument = Text(current, list, "DocumentId");
                if (!string.IsNullOrEmpty(currentDocument) && currentDocument != expectedDocument)
                    throw new InvalidOperationException("Associazione documento della distribuzione cambiata durante la sincronizzazione.");
            }
            if (list == options.LicenseListId)
            {
                string lookup = Field(list, "Distribuzione") + "LookupId";
                if (translated.TryGetValue(lookup, out var target) && current.Fields.TryGetValue(lookup, out var assigned)
                    && assigned.ToString() != target?.ToString())
                    throw new InvalidOperationException("Associazione distribuzione cambiata durante la sincronizzazione.");
                if (Text(current, list, "Stato") == "Sostituita") translated.Remove(Field(list, "Stato"));
            }
            if (translated.All(p => current.Fields.TryGetValue(p.Key, out var item) && item.ToString() == JsonSerializer.SerializeToElement(p.Value).ToString())) return;
            try { await graph.UpdateAsync(list, current.Id, translated, current.ETag, ct); return; }
            catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 2) { }
        }
    }
}
