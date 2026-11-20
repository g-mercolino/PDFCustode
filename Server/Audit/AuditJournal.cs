using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfCustode.Server.Audit;

public sealed record AuditEvent(string EventId, DateTimeOffset DataEvento, string TipoEvento, string Esito, string Title,
    string? DocumentId = null, string? LicenseId = null, string? RequestId = null, string? Details = null);

/// <summary>
/// Private, durable outbox. Retain history and acknowledgement markers together; archive manually.
/// Every operation takes an exclusive cross-process file lock. Data is flushed before atomic rename;
/// directory metadata durability on sudden power loss still depends on the filesystem/volume.
/// </summary>
public sealed class AuditJournal
{
    private const int MaxEventBytes = 16384;
    private readonly string pendingFolder;
    private readonly string historyFolder;
    private readonly string acknowledgedFolder;
    private readonly string lockFile;
    private readonly Lock gate = new();
    private readonly Dictionary<string, HistoryIndex> historyIndexes = new(StringComparer.Ordinal);
    private static readonly string[] AllowedDetailKeys =
    ["reason", "errorType", "statusCode", "attempt", "count", "method", "path", "elapsedMs", "configuredDocuments", "pendingCount"];
    private static readonly Regex CodePattern = Pattern(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])");
    private static readonly Regex BearerPattern = Pattern(@"\bBearer\s+\S+", RegexOptions.IgnoreCase);
    private static readonly Regex SecretPattern = Pattern(@"\b(?:activation_?code|codiceattivazione|access_token|client_secret|password|private_?key|document_?key|wrapped_?key|token)\s*[:=]\s*(?:""[^""]*""|[^\s,;]+)", RegexOptions.IgnoreCase);
    private static readonly Regex EncodedPattern = Pattern(@"[A-Za-z0-9+/=_-]{80,}|[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}");
    private static readonly Regex PrivateKeyPattern = Pattern(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*", RegexOptions.IgnoreCase);
    private static readonly Regex IdentifierPattern = Pattern(@"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}\z");

    public AuditJournal(string root)
    {
        string privateRoot = Path.GetFullPath(root);
        pendingFolder = Path.Combine(privateRoot, "pending");
        historyFolder = Path.Combine(privateRoot, "history");
        acknowledgedFolder = Path.Combine(privateRoot, "acknowledged");
        lockFile = Path.Combine(privateRoot, "audit.lock");
        Directory.CreateDirectory(pendingFolder);
        Directory.CreateDirectory(historyFolder);
        Directory.CreateDirectory(acknowledgedFolder);
    }

    public void Record(AuditEvent entry)
    {
        AuditEvent safe = Sanitize(entry);
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            if (File.Exists(AcknowledgedPath(safe.EventId))) return;
            string pendingPath = PendingPath(safe.EventId);
            if (File.Exists(pendingPath)) safe = ReadEvent(pendingPath, safe.EventId);
            else WriteAtomic(pendingPath, JsonSerializer.SerializeToUtf8Bytes(safe));
            // Pending is committed first, so a crash before/during this append can be replayed.
            EnsureHistory(safe);
        }
    }

    public IReadOnlyList<AuditEvent> Pending() => Pending(200);

    public IReadOnlyList<AuditEvent> Pending(int maximum)
    {
        if (maximum is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            var result = new List<AuditEvent>();
            foreach (string path in Directory.EnumerateFiles(pendingFolder, "*.json"))
            {
                string id = StoredEventId(Path.GetFileNameWithoutExtension(path));
                if (File.Exists(AcknowledgedPath(id)))
                {
                    File.Delete(path);
                    continue;
                }
                AuditEvent item = ReadEvent(path, id);
                result.Add(item);
            }
            var ordered = result.OrderBy(item => item.DataEvento).ThenBy(item => item.EventId, StringComparer.Ordinal).Take(maximum).ToArray();
            foreach (var item in ordered) EnsureHistory(item);
            return ordered;
        }
    }

    public void Acknowledge(string eventId)
    {
        string id = EventId(eventId);
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            string pendingPath = PendingPath(id);
            if (File.Exists(AcknowledgedPath(id)))
            {
                File.Delete(pendingPath);
                return;
            }
            if (!File.Exists(pendingPath)) return;
            EnsureHistory(ReadEvent(pendingPath, id));
            // Keep the durable marker: retries of Record must not resurrect acknowledged events.
            WriteAtomic(AcknowledgedPath(id), Encoding.ASCII.GetBytes(id));
            File.Delete(pendingPath);
        }
    }

    private AuditEvent ReadEvent(string path, string expectedId)
    {
        if (new FileInfo(path).Length > MaxEventBytes) throw InvalidJournal();
        try
        {
            var item = JsonSerializer.Deserialize<AuditEvent>(File.ReadAllBytes(path));
            if (item is null || StoredEventId(item.EventId) != expectedId) throw InvalidJournal();
            return Sanitize(item);
        }
        catch (JsonException) { throw InvalidJournal(); }
    }

    private void EnsureHistory(AuditEvent entry)
    {
        string path = Path.Combine(historyFolder, entry.DataEvento.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Journal files are append-only. Rebuild after another instance changes the length; avoid
        // rescanning a whole day once for every pending item during a prolonged remote outage.
        if (historyIndexes.TryGetValue(path, out var cached) && cached.Length == file.Length)
        {
            if (!cached.EventIds.Contains(entry.EventId))
            {
                AppendHistory(file, entry);
                cached.EventIds.Add(entry.EventId);
                cached.Length = file.Length;
            }
            return;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long lineStart = 0;
        var line = new List<byte>();
        int value;
        while ((value = file.ReadByte()) >= 0)
        {
            if (value == '\n')
            {
                ids.Add(HistoryEventId(line.ToArray()));
                line.Clear();
                lineStart = file.Position;
            }
            else
            {
                if (line.Count >= MaxEventBytes) throw InvalidJournal();
                line.Add((byte)value);
            }
        }
        if (line.Count > 0)
        {
            // Only an incomplete final line can be discarded. Complete earlier records are retained.
            try
            {
                ids.Add(HistoryEventId(line.ToArray()));
                file.WriteByte((byte)'\n');
            }
            catch (InvalidDataException)
            {
                file.SetLength(lineStart);
                file.Position = lineStart;
            }
            file.Flush(flushToDisk: true);
        }
        if (!ids.Contains(entry.EventId))
        {
            AppendHistory(file, entry);
            ids.Add(entry.EventId);
        }
        else file.Flush(flushToDisk: true);
        if (historyIndexes.Count >= 4) historyIndexes.Clear();
        historyIndexes[path] = new HistoryIndex(file.Length, ids);
    }

    private static void AppendHistory(FileStream file, AuditEvent entry)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (bytes.Length > MaxEventBytes) throw InvalidJournal();
        file.Position = file.Length;
        file.Write(bytes);
        file.WriteByte((byte)'\n');
        file.Flush(flushToDisk: true);
    }

    private static string HistoryEventId(byte[] line)
    {
        try
        {
            using var parsed = JsonDocument.Parse(line);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("EventId", out var id) || id.ValueKind != JsonValueKind.String)
                throw InvalidJournal();
            return StoredEventId(id.GetString());
        }
        catch (JsonException) { throw InvalidJournal(); }
    }

    private static AuditEvent Sanitize(AuditEvent entry) => entry with
    {
        EventId = EventId(entry.EventId), DataEvento = entry.DataEvento.ToUniversalTime(),
        TipoEvento = Text(entry.TipoEvento, 64), Esito = Text(entry.Esito, 32), Title = Text(entry.Title, 200),
        DocumentId = Identifier(entry.DocumentId), LicenseId = Identifier(entry.LicenseId), RequestId = Identifier(entry.RequestId),
        Details = Details(entry.Details)
    };

    private static string? Details(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 8192) return null;
        try
        {
            using var parsed = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return null;
            var safe = new Dictionary<string, object?>();
            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                string? key = AllowedDetailKeys.FirstOrDefault(key => key.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
                if (key is null) continue;
                var value = property.Value;
                object? scalar;
                switch (value.ValueKind)
                {
                    case JsonValueKind.String: scalar = Text(value.GetString(), 128); break;
                    case JsonValueKind.Number when value.TryGetInt64(out long number): scalar = number; break;
                    case JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number): scalar = number; break;
                    case JsonValueKind.True: scalar = true; break;
                    case JsonValueKind.False: scalar = false; break;
                    default: continue;
                }
                safe[key] = scalar;
                if (JsonSerializer.Serialize(safe).Length > 2048) safe.Remove(key);
            }
            return safe.Count == 0 ? null : JsonSerializer.Serialize(safe);
        }
        catch (JsonException) { return null; }
    }

    private static string Text(string? input, int maximum)
    {
        if (input is null) return "";
        string text = new(input.Take(8192).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        foreach (var pattern in new[] { PrivateKeyPattern, BearerPattern, SecretPattern, CodePattern, EncodedPattern })
            text = pattern.Replace(text, "[redacted]");
        return text[..Math.Min(text.Length, maximum)].Trim();
    }

    // These fields are supplied as identifiers by callers, not as request/exception bodies.
    private static string? Identifier(string? input) => input is not null && IdentifierPattern.IsMatch(input) ? input : null;
    private string PendingPath(string id) => Path.Combine(pendingFolder, id + ".json");
    private string AcknowledgedPath(string id) => Path.Combine(acknowledgedFolder, id + ".ack");
    private static string EventId(string? input) =>
        input is not null && (Guid.TryParseExact(input, "N", out var id) || Guid.TryParseExact(input, "D", out id)) && id != Guid.Empty
            ? id.ToString("N") : throw new ArgumentException("Identificativo evento non valido.", nameof(input));
    private static string StoredEventId(string? input)
    {
        try { return EventId(input); }
        catch (ArgumentException) { throw InvalidJournal(); }
    }
    private static InvalidDataException InvalidJournal() => new("Archivio di audit non valido.");
    private static Regex Pattern(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private FileStream AcquireFileLock()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { Thread.Sleep(25); }
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }

    private sealed class HistoryIndex(long length, HashSet<string> eventIds)
    {
        public long Length { get; set; } = length;
        public HashSet<string> EventIds { get; } = eventIds;
    }
}
