using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfCustode.Core;
using PdfCustode.Server.Audit;

namespace PdfCustode.Server.SharePoint;

/// <summary>Projects signed local activations into an operator-owned annual license register.</summary>
public sealed class SharePointSynchronizer(
    IGraphLists graph, SharePointOptions options, ActivationCatalog catalog,
    AuditJournal journal, ILogger<SharePointSynchronizer> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, Dictionary<string, string>> fieldNames = [];
    private static readonly string AdministrativeExpiry = new DateTimeOffset(2100, 12, 31, 0, 0, 0, TimeSpan.Zero).ToString("O");
    public int QuotaYear => options.QuotaYear;

    public async Task ValidateAsync(CancellationToken ct)
    {
        if (QuotaYear is < 1 or > 9999 || options.InitialLicenseTotal < 0)
            throw new InvalidOperationException("Configurare QuotaYear e InitialLicenseTotal validi per SharePoint.");
        var names = new Dictionary<string, Dictionary<string, string>>
        {
            [options.DistributionListId] = await ValidateList(options.DistributionListId,
                ["Title:text", "Anno:number", "LicenzeTotali:number", "LicenzeConsumate|LicenzeImpegnate:number"],
                ["LicenzeDisponibili:calculated"], "Anno", ct),
            [options.LicenseListId] = await ValidateList(options.LicenseListId,
                ["Title:text", "Nome:text", "Cognome:text", "CodiceUtente:text", "NomeMacchina:text", "Piattaforma:text|choice",
                    "CodiceHash:text", "LicenseId:text", "Stato:choice", "DeviceKeyHash:text", "DataInizio:dateTime", "DataFine:dateTime"],
                ["CodiceAttivazione:text", "ScadenzaTecnica:dateTime"], "CodiceHash", ct),
            [options.LogListId] = await ValidateList(options.LogListId,
                ["Title:text", "EventId:text", "DataEvento:dateTime", "TipoEvento:choice", "Esito:choice", "LicenseId:text", "RequestId:text", "Dettagli:text"],
                ["Distribuzione:lookup"], "EventId", ct)
        };
        var snapshots = catalog.GetSnapshots();
        if (options.DocumentIds is null || options.DocumentIds.Count == 0
            || options.DocumentIds.Any(id => string.IsNullOrWhiteSpace(id) || !snapshots.Any(s => s.DocumentId == id))
            || options.DocumentIds.Distinct(StringComparer.Ordinal).Count() != options.DocumentIds.Count)
            throw new InvalidOperationException("DocumentIds SharePoint deve contenere documenti univoci presenti nell'archivio amministrativo locale.");
        // Publish only a complete, validated mapping, so a failed preflight cannot leave a partial schema.
        fieldNames = names;
    }

    private async Task<Dictionary<string, string>> ValidateList(string list, string[] required, string[] optional, string unique, CancellationToken ct)
    {
        var columns = await graph.GetColumnsAsync(list, ct);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in required.Concat(optional))
        {
            var parts = definition.Split(':');
            var aliases = parts[0].Split('|');
            string name = aliases[0];
            // SharePoint exposes Title, LinkTitle and LinkTitleNoMenu with the same display label.
            // Prefer the API-facing internal name; use display aliases only when no internal name matches.
            var matches = columns.Where(c => aliases.Any(alias => string.Equals(c.Name, alias, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length == 0)
                matches = columns.Where(c => aliases.Any(alias => string.Equals(c.DisplayName, alias, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length == 0 && optional.Contains(definition)) continue;
            if (matches.Length != 1) throw new InvalidOperationException($"Colonna SharePoint mancante o ambigua: {parts[0]}.");
            var column = matches[0];
            if (!parts[1].Split('|').Contains(column.Type) || column.ReadOnly && column.Type != "calculated")
                throw new InvalidOperationException($"Tipo colonna SharePoint errato: {name}, richiesto {parts[1]} modificabile.");
            if (name == unique && (!column.EnforceUniqueValues || !column.Indexed))
                throw new InvalidOperationException($"Abilitare valori univoci e indice su {name} per consentire tentativi ripetuti senza duplicati.");
            if (column.Type == "lookup")
            {
                if (column.LookupListId is not null && !SameList(column.LookupListId, options.DistributionListId))
                    throw new InvalidOperationException("La colonna Distribuzione deve puntare a PDF_Distribuzioni.");
                if (column.LookupAllowsMultiple) throw new InvalidOperationException("La colonna Distribuzione deve essere una lookup singola.");
            }
            string[]? requiredChoices = name switch
            {
                "Stato" => ["Attiva", "Scaduta", "Sostituita"],
                "Piattaforma" when column.Type == "choice" => ["Windows", "Android"],
                "Esito" => ["Successo", "Rifiutato", "Errore"],
                "TipoEvento" => ["CodiceGenerato", "CodiceAssegnato", "Attivazione", "Rinnovo", "Sostituzione", "ErroreTecnico"],
                _ => null
            };
            if (column.Choices is not null && requiredChoices is not null && requiredChoices.Any(c => !column.Choices.Contains(c)))
                throw new InvalidOperationException($"Valori Choice mancanti nella colonna {name}.");
            mapping.Add(name, column.Name);
        }
        return mapping;
    }

    private static bool SameList(string left, string right) => string.Equals(left.Trim('{', '}'), right.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);
    private string Field(string list, string name) => fieldNames[list][name];
    private bool HasField(string list, string name) => fieldNames[list].ContainsKey(name);
    private Dictionary<string, object?> Fields(string list, Dictionary<string, object?> fields) => fields.ToDictionary(
        p => p.Key.EndsWith("LookupId", StringComparison.Ordinal) ? Field(list, p.Key[..^8]) + "LookupId" : Field(list, p.Key), p => p.Value);
    private string? Text(GraphItem row, string list, string name) =>
        row.Fields.TryGetValue(Field(list, name), out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    public async Task<int> GetQuotaAsync(string documentId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!options.DocumentIds.Contains(documentId, StringComparer.Ordinal))
                throw new InvalidOperationException("Documento privo di configurazione SharePoint in DocumentIds.");
            if (fieldNames.Count != 3) await ValidateAsync(ct);
            var annual = await EnsureCurrentAnnual(ct);
            return NonNegativeInteger(annual, options.DistributionListId, "LicenzeTotali");
        }
        finally { gate.Release(); }
    }

    public async Task SynchronizeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await ValidateAsync(ct);
            var snapshots = catalog.GetSnapshots();
            var licenseYears = snapshots.SelectMany(s => s.Codes).Where(c => c.License is not null)
                .ToDictionary(c => Payload(c).LicenseId, c => c.EffectiveQuotaYear!.Value, StringComparer.Ordinal);
            var years = licenseYears.Values.Append(QuotaYear).Distinct().ToArray();
            var annualRows = new Dictionary<int, GraphItem>();
            // Historical rows need an operator-provisioned total; do this preflight before projecting licenses or counters.
            foreach (int year in years.Where(y => y != QuotaYear))
                annualRows[year] = await FindAnnual(year, ct) ?? throw MissingAnnual(year);
            annualRows[QuotaYear] = await EnsureCurrentAnnual(ct);
            foreach (var document in snapshots)
            {
                foreach (var code in document.Codes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (code.License is not null) await SyncLicense(code, ct);
                    journal.Record(new(StableId("code-discovered:" + document.DocumentId + ":" + code.CodeHash), DateTimeOffset.UtcNow,
                        "CodiceGenerato", "Successo", "Codice rilevato nel registro locale", document.DocumentId,
                        Details: JsonSerializer.Serialize(new { reason = "ExistingCodeDiscovered" })));
                    if (code.License is not null)
                    {
                        var payload = Payload(code);
                        journal.Record(new(StableId("activated:" + payload.LicenseId), DateTimeOffset.Parse(payload.IssuedAt, CultureInfo.InvariantCulture),
                            "Attivazione", "Successo", "Attivazione persistita", document.DocumentId, payload.LicenseId,
                            Details: JsonSerializer.Serialize(new { reason = "PersistedLicense" })));
                    }
                }
            }
            foreach (int year in annualRows.Keys) await SyncAnnualConsumption(year, ct);
            foreach (var entry in journal.Pending().Take(200))
            {
                ct.ThrowIfCancellationRequested();
                var fields = new Dictionary<string, object?>
                {
                    ["Title"] = entry.Title, ["EventId"] = entry.EventId, ["DataEvento"] = entry.DataEvento.ToUniversalTime().ToString("O"),
                    ["TipoEvento"] = entry.TipoEvento, ["Esito"] = entry.Esito, ["LicenseId"] = entry.LicenseId,
                    ["RequestId"] = entry.RequestId, ["Dettagli"] = entry.Details
                };
                if (HasField(options.LogListId, "Distribuzione") && EventYear(entry, licenseYears) is int year)
                {
                    if (!annualRows.TryGetValue(year, out var annual)) annual = await FindAnnual(year, ct) ?? throw MissingAnnual(year);
                    fields["DistribuzioneLookupId"] = annual.Id;
                }
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
            logger.LogInformation("SharePoint annual sync completed; configured quota year {Year}", QuotaYear);
        }
        finally { gate.Release(); }
    }

    private int? EventYear(AuditEvent entry, IReadOnlyDictionary<string, int> licenseYears) =>
        entry.LicenseId is not null && licenseYears.TryGetValue(entry.LicenseId, out int year) ? year
        : entry.TipoEvento == "CodiceGenerato" ? QuotaYear : null;

    private static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();
    private static LicensePayload Payload(ActivationSnapshot code) => JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(code.License!.Payload))
        ?? throw new InvalidDataException("Licenza locale non valida.");
    private static InvalidOperationException MissingAnnual(int year) => new($"Riga annuale PDF_Distribuzioni mancante per Anno {year}. Crearla con LicenzeTotali esplicito prima di sincronizzare lo storico.");

    private async Task<GraphItem?> FindAnnual(int year, CancellationToken ct)
    {
        // Graph's existing string filter is unsuitable for Anno (number). Annual rows are a small list.
        var matches = (await graph.GetItemsAsync(options.DistributionListId, ct))
            .Where(row => row.Fields.TryGetValue(Field(options.DistributionListId, "Anno"), out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && number == year).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"Più righe PDF_Distribuzioni per Anno {year}: ripristinare l'unicità prima di sincronizzare.");
        return matches.SingleOrDefault();
    }

    private async Task<GraphItem> EnsureCurrentAnnual(CancellationToken ct)
    {
        var row = await FindAnnual(QuotaYear, ct);
        if (row is not null) return row;
        try
        {
            await graph.CreateAsync(options.DistributionListId, Fields(options.DistributionListId, new()
            {
                ["Title"] = "Licenze " + QuotaYear.ToString(CultureInfo.InvariantCulture), ["Anno"] = QuotaYear,
                ["LicenzeTotali"] = options.InitialLicenseTotal, ["LicenzeConsumate"] = 0
            }), ct);
        }
        catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict) { }
        return await FindAnnual(QuotaYear, ct) ?? throw new InvalidOperationException("Riga annuale creata o concorrente non ancora leggibile. Riprovare la sincronizzazione.");
    }

    private int NonNegativeInteger(GraphItem row, string list, string name)
    {
        if (!row.Fields.TryGetValue(Field(list, name), out var raw) || raw.ValueKind != JsonValueKind.Number || !raw.TryGetDecimal(out var number)
            || number < 0 || number > int.MaxValue || decimal.Truncate(number) != number)
            throw new InvalidOperationException($"{name} deve essere un numero intero non negativo.");
        return (int)number;
    }

    private async Task SyncAnnualConsumption(int year, CancellationToken ct)
    {
        string list = options.DistributionListId;
        for (int attempt = 0; ; attempt++)
        {
            var current = await FindAnnual(year, ct) ?? throw MissingAnnual(year);
            if (string.IsNullOrEmpty(current.ETag)) throw new InvalidOperationException("ETag SharePoint assente.");
            // Refresh all catalog documents after reading the ETag. A concurrent newer projection forces a retry.
            int consumed = catalog.GetSnapshots().SelectMany(d => d.Codes).Count(c => c.License is not null && c.EffectiveQuotaYear == year);
            if (NonNegativeInteger(current, list, "LicenzeConsumate") == consumed) return;
            try
            {
                await graph.UpdateAsync(list, current.Id, Fields(list, new() { ["LicenzeConsumate"] = consumed }), current.ETag, ct);
                return;
            }
            catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 2) { }
        }
    }

    private async Task SyncLicense(ActivationSnapshot code, CancellationToken ct)
    {
        string list = options.LicenseListId;
        var existing = await graph.FindAsync(list, Field(list, "CodiceHash"), code.CodeHash, ct);
        var payload = Payload(code);
        var values = new Dictionary<string, object?>
        {
            ["CodiceHash"] = code.CodeHash, ["LicenseId"] = payload.LicenseId, ["DeviceKeyHash"] = payload.DeviceKeyHash,
            ["DataInizio"] = payload.IssuedAt, ["DataFine"] = AdministrativeExpiry, ["Stato"] = "Attiva"
        };
        if (HasField(list, "CodiceAttivazione") && code.ActivationCode is not null) values["CodiceAttivazione"] = code.ActivationCode;
        if (HasField(list, "ScadenzaTecnica")) values["ScadenzaTecnica"] = Crypto.GetExpiration(payload).ToUniversalTime().ToString("O");
        if (code.Profile is { } profile)
        {
            values["Nome"] = profile.Nome; values["Cognome"] = profile.Cognome; values["CodiceUtente"] = profile.CodiceUtente;
            values["NomeMacchina"] = profile.NomeMacchina; values["Piattaforma"] = profile.Piattaforma;
        }
        if (existing is null)
        {
            values["Title"] = "LIC-" + code.CodeHash[..12];
            try { await graph.CreateAsync(list, Fields(list, values), ct); return; }
            catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                if (await graph.FindAsync(list, Field(list, "CodiceHash"), code.CodeHash, ct) is null)
                    throw new InvalidOperationException("Conflitto SharePoint senza una riga licenza recuperabile.");
            }
        }
        values.Remove("Title");
        await PatchLicense(code.CodeHash, values, ct);
    }

    private async Task PatchLicense(string codeHash, Dictionary<string, object?> values, CancellationToken ct)
    {
        string list = options.LicenseListId;
        for (int attempt = 0; ; attempt++)
        {
            var current = await graph.FindAsync(list, Field(list, "CodiceHash"), codeHash, ct)
                ?? throw new InvalidOperationException("Riga SharePoint rimossa durante la sincronizzazione.");
            if (string.IsNullOrEmpty(current.ETag)) throw new InvalidOperationException("ETag SharePoint assente.");
            foreach (string identity in new[] { "LicenseId", "DeviceKeyHash" })
                if (Text(current, list, identity) is { Length: > 0 } assigned && assigned != values[identity]?.ToString())
                    throw new InvalidOperationException($"CodiceHash già associato a {identity} differente.");
            var fields = new Dictionary<string, object?>(values);
            // Initial profile comes from the activation form; subsequent operator corrections belong to the register.
            foreach (string profile in new[] { "Nome", "Cognome", "CodiceUtente", "NomeMacchina", "Piattaforma" })
                if (!string.IsNullOrEmpty(Text(current, list, profile))) fields.Remove(profile);
            if (Text(current, list, "Stato") is { Length: > 0 } state && state is not "Disponibile" and not "Assegnata") fields.Remove("Stato");
            var translated = Fields(list, fields);
            if (translated.All(p => current.Fields.TryGetValue(p.Key, out var item) && item.ToString() == JsonSerializer.SerializeToElement(p.Value).ToString())) return;
            try { await graph.UpdateAsync(list, current.Id, translated, current.ETag, ct); return; }
            catch (GraphApiException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 2) { }
        }
    }
}
