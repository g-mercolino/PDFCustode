using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;

namespace PdfCustode.Server.SharePoint;

public sealed record GraphColumn(string Name, string DisplayName, string Type, bool ReadOnly, bool Indexed, bool EnforceUniqueValues, string? LookupListId = null)
{
    public IReadOnlyList<string>? Choices { get; init; }
    public bool LookupAllowsMultiple { get; init; }
}

public sealed record GraphItem(string Id, string? ETag, Dictionary<string, JsonElement> Fields);

public interface IGraphLists
{
    Task<IReadOnlyList<GraphColumn>> GetColumnsAsync(string listId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GraphItem>> GetItemsAsync(string listId, CancellationToken cancellationToken);
    Task<GraphItem?> FindAsync(string listId, string field, string value, CancellationToken cancellationToken);
    Task<GraphItem> CreateAsync(string listId, IReadOnlyDictionary<string, object?> fields, CancellationToken cancellationToken);
    Task UpdateAsync(string listId, string itemId, IReadOnlyDictionary<string, object?> fields, string? etag, CancellationToken cancellationToken);
}

// This exception deliberately never retains remote response bodies or inner exceptions.
public sealed class GraphApiException(HttpStatusCode statusCode, bool retryable, string errorCode = "graph_request_failed", TimeSpan? retryAfter = null)
    : Exception($"Microsoft Graph request failed ({(int)statusCode}; {errorCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool Retryable { get; } = retryable;
    public string ErrorCode { get; } = errorCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class GraphListsClient : IGraphLists
{
    const int MaximumAttempts = 4;
    static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    static readonly string[] ColumnTypes = ["calculated", "lookup", "choice", "text", "number", "dateTime", "boolean", "currency", "personOrGroup", "hyperlinkOrPicture", "geolocation", "term", "thumbnail", "contentApprovalStatus"];
    readonly HttpClient client;
    readonly TokenCredential credential;
    readonly string sitePath;

    // Production callers must use a transport with automatic redirects disabled (see GraphAuthentication).
    public GraphListsClient(HttpClient client, TokenCredential credential, SharePointOptions options)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SiteId);
        sitePath = "https://graph.microsoft.com/v1.0/sites/" + Segment(options.SiteId) + "/lists/";
    }

    public async Task<IReadOnlyList<GraphColumn>> GetColumnsAsync(string listId, CancellationToken cancellationToken)
    {
        var values = await GetCollectionAsync(ListPath(listId) + "/columns", cancellationToken);
        try
        {
            return values.Select(column =>
            {
                string type = ColumnTypes.FirstOrDefault(type => column.TryGetProperty(type, out _)) ?? "unknown";
                string? lookupListId = null;
                bool lookupAllowsMultiple = false;
                IReadOnlyList<string>? choices = null;
                if (column.TryGetProperty("lookup", out var lookup))
                {
                    lookupListId = OptionalString(lookup, "listId");
                    lookupAllowsMultiple = Boolean(lookup, "allowMultipleValues");
                }
                if (column.TryGetProperty("choice", out var choice) && choice.TryGetProperty("choices", out var choiceValues))
                    choices = choiceValues.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                string name = RequiredString(column, "name");
                return new GraphColumn(name, OptionalString(column, "displayName") ?? name, type,
                    Boolean(column, "readOnly"), Boolean(column, "indexed"), Boolean(column, "enforceUniqueValues"), lookupListId)
                { Choices = choices, LookupAllowsMultiple = lookupAllowsMultiple };
            }).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw InvalidResponse(); }
    }

    public async Task<IReadOnlyList<GraphItem>> GetItemsAsync(string listId, CancellationToken cancellationToken)
        => (await GetCollectionAsync(ListPath(listId) + "/items?$expand=fields", cancellationToken)).Select(ParseItem).ToArray();

    public async Task<GraphItem?> FindAsync(string listId, string field, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(field) || !Regex.IsMatch(field, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A valid SharePoint internal field name is required.", nameof(field));
        ArgumentNullException.ThrowIfNull(value);
        string filter = $"fields/{field} eq '{value.Replace("'", "''", StringComparison.Ordinal)}'";
        var items = await GetCollectionAsync(ListPath(listId) + "/items?$expand=fields&$filter=" + Uri.EscapeDataString(filter), cancellationToken);
        if (items.Count > 1) throw new GraphApiException(HttpStatusCode.Conflict, false, "graph_ambiguous_unique_key");
        return items.Count == 0 ? null : ParseItem(items[0]);
    }

    public async Task<GraphItem> CreateAsync(string listId, IReadOnlyDictionary<string, object?> fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        using var response = await SendAsync(HttpMethod.Post, new Uri(ListPath(listId) + "/items"), JsonSerializer.Serialize(new { fields }), null, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        return ParseItem(document.RootElement);
    }

    public async Task UpdateAsync(string listId, string itemId, IReadOnlyDictionary<string, object?> fields, string? etag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        using var response = await SendAsync(HttpMethod.Patch, new Uri(ListPath(listId) + "/items/" + Segment(itemId) + "/fields"), JsonSerializer.Serialize(fields), etag, cancellationToken);
    }

    async Task<List<JsonElement>> GetCollectionAsync(string url, CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Uri? next = new(url);
        while (next is not null)
        {
            ValidateGraphUri(next);
            if (!visited.Add(next.AbsoluteUri) || visited.Count > 10000)
                throw new GraphApiException(HttpStatusCode.BadGateway, false, "graph_paging_cycle");
            using var response = await SendAsync(HttpMethod.Get, next, null, null, cancellationToken);
            using var document = await ReadJsonAsync(response, cancellationToken);
            try
            {
                foreach (var item in document.RootElement.GetProperty("value").EnumerateArray()) values.Add(item.Clone());
                string? link = OptionalString(document.RootElement, "@odata.nextLink");
                next = null;
                if (!string.IsNullOrEmpty(link))
                {
                    if (!Uri.TryCreate(link, UriKind.Absolute, out next)) throw InvalidResponse();
                    ValidateGraphUri(next);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            { throw InvalidResponse(); }
        }
        return values;
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? body, string? etag, CancellationToken cancellationToken)
    {
        ValidateGraphUri(uri);
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccessToken token;
            try { token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { throw new GraphApiException(HttpStatusCode.Unauthorized, true, "graph_authentication_failed"); }
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new GraphApiException(HttpStatusCode.RequestTimeout, true, "graph_transport_timeout"); }
            catch (HttpRequestException) { throw new GraphApiException(HttpStatusCode.ServiceUnavailable, true, "graph_transport_failed"); }
            // Never follow any redirect. Authentication is attached only to a validated Graph origin.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new GraphApiException(status, false, "graph_redirect_rejected");
            }
            if (response.IsSuccessStatusCode) return response;
            bool retryable = IsTransient(response.StatusCode);
            TimeSpan delay = RetryDelay(response.Headers.RetryAfter, attempt);
            if (!retryable || attempt + 1 >= MaximumAttempts || delay > MaximumRetryDelay)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new GraphApiException(status, retryable, retryAfter: retryable ? delay : null);
            }
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (retryAfter?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is { } date) return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout;
    static void ValidateGraphUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
            throw new GraphApiException(HttpStatusCode.BadGateway, false, "graph_untrusted_url");
    }

    static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException or InvalidOperationException)
        { throw InvalidResponse(); }
    }

    static GraphItem ParseItem(JsonElement item)
    {
        try
        {
            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("fields", out var values))
                foreach (var property in values.EnumerateObject()) fields.Add(property.Name, property.Value.Clone());
            return new(RequiredString(item, "id"), OptionalString(item, "eTag") ?? OptionalString(item, "@odata.etag"), fields);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        { throw InvalidResponse(); }
    }

    string ListPath(string listId) => sitePath + Segment(listId);
    static string Segment(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return Uri.EscapeDataString(value); }
    static bool Boolean(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.GetBoolean();
    static string? OptionalString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;
    static string RequiredString(JsonElement value, string name) => OptionalString(value, name) is { Length: > 0 } result ? result : throw InvalidResponse();
    static GraphApiException InvalidResponse() => new(HttpStatusCode.BadGateway, false, "graph_invalid_response");
}
