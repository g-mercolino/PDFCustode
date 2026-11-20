using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using PdfCustode.Server.SharePoint;

static class GraphTests
{
    public static async Task RunAsync()
    {
        await Paging(); await Columns(); await Find(); await Writes(); await Retries();
        await UntrustedUris(); await SafeErrors(); await NetworkFailure(); await Cancellation(); Options();
        Reject<InvalidOperationException>(() => GraphAuthentication.CreateCredential(new() { Authentication = "DefaultAzureCredential" }));
        Reject<InvalidOperationException>(() => GraphAuthentication.CreateCredential(new() { Authentication = "Certificate" }));
        Reject<InvalidOperationException>(() => GraphAuthentication.CreateCredential(new() { Authentication = "Certificate", TenantId = "tenant", ClientId = "client", CertificateThumbprint = "thumb", CertificateStoreLocation = "Other" }));
        Assert(GraphAuthentication.CreateCredential(new() { Authentication = "ManagedIdentity" }) is ManagedIdentityCredential, "explicit system managed identity");
        Assert(GraphAuthentication.CreateCredential(new() { Authentication = "ManagedIdentity", ManagedIdentityClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" }) is ManagedIdentityCredential, "explicit user managed identity");
        Console.WriteLine("Graph transport tests passed.");
    }
    static void Options()
    {
        new SharePointOptions().Validate();
        Reject<InvalidOperationException>(() => new SharePointOptions { Enabled = true }.Validate());
        var valid = new SharePointOptions
        {
            Enabled = true, Authentication = "ManagedIdentity", SiteId = "innowatedev.sharepoint.com,aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa,bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            DistributionListId = "11111111-1111-1111-1111-111111111111", LicenseListId = "22222222-2222-2222-2222-222222222222", LogListId = "33333333-3333-3333-3333-333333333333",
            Distributions = [new() { DocumentId = "document", DistribuzioneId = "distribution", LicenzeTotali = 2 }]
        };
        valid.Validate();
        valid.Distributions.Add(new() { DocumentId = "document", DistribuzioneId = "second" });
        Reject<InvalidOperationException>(valid.Validate);
        valid.Distributions.RemoveAt(1);
        valid.LicenseListId = valid.LogListId;
        Reject<InvalidOperationException>(valid.Validate);
        valid.LicenseListId = "22222222-2222-2222-2222-222222222222";
        valid.SiteHostname = "other.sharepoint.com";
        Reject<InvalidOperationException>(valid.Validate);
    }
    static async Task Paging()
    {
        var handler = new Handler((_, n) => Response(n == 1
            ? """{"value":[{"id":"1","eTag":"\"v1\"","fields":{"Total":4,"Active":true,"Missing":null}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/sites/site/lists/list/items?$skiptoken=next"}"""
            : """{"value":[{"id":"2","fields":{"Title":"second"}}]}"""));
        var credential = new Credential();
        var items = await Client(handler, credential).GetItemsAsync("list", default);
        Assert(items.Count == 2 && items[0].ETag == "\"v1\"", "all pages and ETag");
        Assert(items[0].Fields["Total"].GetInt32() == 4 && items[0].Fields["Active"].GetBoolean() && items[0].Fields["Missing"].ValueKind == JsonValueKind.Null, "typed fields survive disposed response");
        Assert(credential.Scopes.Count > 0 && credential.Scopes.All(x => x == "https://graph.microsoft.com/.default"), "Graph resource scope only");
        Assert(handler.Requests.All(r => r.Authorization == "Bearer local-test-token" && r.Uri.Host == "graph.microsoft.com"), "token only for Graph requests");
    }
    static async Task Columns()
    {
        var handler = new Handler((_, _) => Response("""{"value":[{"name":"Distribution","displayName":"Distribuzione","indexed":true,"lookup":{"listId":"target-list","allowMultipleValues":false}},{"name":"Available","displayName":"Disponibili","readOnly":true,"calculated":{"formula":"=[Total]-[Used]","outputType":"number"}},{"name":"Hash","displayName":"Hash","indexed":true,"enforceUniqueValues":true,"text":{}},{"name":"Status","displayName":"Stato","choice":{"choices":["Active","Expired"]}}]}"""));
        var columns = await Client(handler).GetColumnsAsync("list", default);
        Assert(columns[0].Type == "lookup" && columns[0].LookupListId == "target-list" && columns[0].Indexed && !columns[0].LookupAllowsMultiple, "lookup target metadata");
        Assert(columns[1].Type == "calculated" && columns[1].ReadOnly, "computed metadata");
        Assert(columns[2].EnforceUniqueValues && columns[2].Type == "text", "unique text metadata");
        Assert(columns[3].Choices!.SequenceEqual(new[] { "Active", "Expired" }), "choice metadata");
    }
    static async Task Find()
    {
        var handler = new Handler((_, _) => Response("""{"value":[{"id":"1","fields":{"Title":"found"}}]}"""));
        Assert((await Client(handler).FindAsync("list", "CodiceHash", "O'Brien & x", default))?.Id == "1", "find result");
        Assert(Uri.UnescapeDataString(handler.Requests[0].Uri.Query).Contains("fields/CodiceHash eq 'O''Brien & x'", StringComparison.Ordinal), "OData quote escape and URL encoding");
        await RejectAsync<ArgumentException>(() => Client(handler).FindAsync("list", "Title or true", "x", default));
        var multiple = new Handler((_, _) => Response("""{"value":[{"id":"1","fields":{}},{"id":"2","fields":{}}]}"""));
        Assert(!(await RejectAsync<GraphApiException>(() => Client(multiple).FindAsync("list", "CodiceHash", "x", default))).Retryable, "ambiguous unique key fails closed");
    }
    static async Task Writes()
    {
        var handler = new Handler((_, n) => n == 1 ? Response("""{"id":"5","eTag":"\"v2\"","fields":{"Total":7}}""", HttpStatusCode.Created) : new(HttpStatusCode.NoContent));
        var client = Client(handler);
        var created = await client.CreateAsync("list", new Dictionary<string, object?> { ["Total"] = 7, ["Used"] = false, ["Date"] = null }, default);
        await client.UpdateAsync("list", created.Id, new Dictionary<string, object?> { ["Total"] = 8 }, "\"v2\"", default);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert(body.RootElement.GetProperty("fields").GetProperty("Total").GetInt32() == 7 && body.RootElement.GetProperty("fields").GetProperty("Used").ValueKind == JsonValueKind.False, "typed create wrapper");
        Assert(handler.Requests[1].Method == HttpMethod.Patch && handler.Requests[1].IfMatch == "\"v2\"" && handler.Requests[1].Uri.AbsolutePath.EndsWith("/items/5/fields", StringComparison.Ordinal), "fields patch with ETag");
        var conflict = new Handler((_, _) => Response("secret", HttpStatusCode.PreconditionFailed));
        Assert((await RejectAsync<GraphApiException>(() => Client(conflict).UpdateAsync("list", "5", new Dictionary<string, object?>(), "\"v1\"", default))).StatusCode == HttpStatusCode.PreconditionFailed && conflict.Requests.Count == 1, "412 caller re-read");
    }
    static async Task Retries()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout, HttpStatusCode.RequestTimeout })
        {
            var handler = new Handler((_, n) => n == 1 ? Transient(status) : Response("""{"value":[]}"""));
            await Client(handler).GetItemsAsync("list", default);
            Assert(handler.Requests.Count == 2, $"retry {(int)status}");
        }
        var bounded = new Handler((_, _) => Transient(HttpStatusCode.TooManyRequests));
        Assert((await RejectAsync<GraphApiException>(() => Client(bounded).GetItemsAsync("list", default))).Retryable && bounded.Requests.Count is >= 2 and <= 5, "bounded retries");
        var longWait = new Handler((_, _) => { var response = Transient(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromHours(1)); return response; });
        var delayed = await RejectAsync<GraphApiException>(() => Client(longWait).GetItemsAsync("list", default));
        Assert(delayed.Retryable && longWait.Requests.Count == 1, "long Retry-After returns without early retry");
        Assert(delayed.RetryAfter == TimeSpan.FromHours(1), "server Retry-After remains available to worker");
    }
    static async Task UntrustedUris()
    {
        foreach (string next in new[] { "https://evil.example/v1.0/page", "http://graph.microsoft.com/v1.0/page", "https://graph.microsoft.com:444/v1.0/page", "https://user@graph.microsoft.com/v1.0/page", "https://graph.microsoft.com/beta/page" })
        {
            var handler = new Handler((_, _) => Response(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next })));
            await RejectAsync<GraphApiException>(() => Client(handler).GetItemsAsync("list", default));
            Assert(handler.Requests.Count == 1, "untrusted nextLink never fetched");
        }
        var redirect = new Handler((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("https://evil.example"); return response; });
        await RejectAsync<GraphApiException>(() => Client(redirect).GetItemsAsync("list", default));
        Assert(redirect.Requests.Count == 1, "redirect rejected");
        var cycle = new Handler((_, _) => Response("""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/sites/site/lists/list/items?$expand=fields"}"""));
        await RejectAsync<GraphApiException>(() => Client(cycle).GetItemsAsync("list", default));
        Assert(cycle.Requests.Count <= 2, "paging cannot cycle");
    }
    static async Task SafeErrors()
    {
        var handler = new Handler((_, _) => Response("{\"error\":{\"message\":\"private-code bearer secret\"}}", HttpStatusCode.Forbidden));
        var error = await RejectAsync<GraphApiException>(() => Client(handler).GetItemsAsync("list", default));
        Assert(error.StatusCode == HttpStatusCode.Forbidden && !error.Retryable && !error.ToString().Contains("private-code"), "remote payload never diagnostic");
        error = await RejectAsync<GraphApiException>(() => Client(handler, new Credential { Failure = new AuthenticationFailedException("private-code bearer secret") }).GetItemsAsync("list", default));
        Assert(!error.ToString().Contains("private-code") && error.InnerException is null, "auth error sanitized");
        error = await RejectAsync<GraphApiException>(() => Client(new Handler((_, _) => Response("private-code bearer secret"))).GetItemsAsync("list", default));
        Assert(!error.ToString().Contains("private-code"), "invalid response sanitized");
    }
    static async Task NetworkFailure()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("private-code bearer secret"));
        var error = await RejectAsync<GraphApiException>(() => Client(handler).CreateAsync("list", new Dictionary<string, object?> { ["Title"] = "x" }, default));
        Assert(handler.Requests.Count == 1 && error.Retryable && !error.ToString().Contains("private-code"), "uncertain POST never replayed");
    }
    static async Task Cancellation()
    {
        var handler = new Handler((_, _) => { var response = Transient(HttpStatusCode.ServiceUnavailable); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(10)); return response; });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await RejectAsync<OperationCanceledException>(() => Client(handler).GetItemsAsync("list", cancel.Token));
        Assert(handler.Requests.Count == 1, "cancellation stops retry");
    }
    static GraphListsClient Client(Handler handler, Credential? credential = null) => new(new HttpClient(handler), credential ?? new Credential(), new() { SiteId = "site" });
    static HttpResponseMessage Response(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    static HttpResponseMessage Transient(HttpStatusCode code) { var response = Response("secret-payload", code); response.Headers.RetryAfter = new(TimeSpan.Zero); return response; }
    static void Assert(bool condition, string name) { if (!condition) throw new Exception(name); }
    static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    static async Task<T> RejectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T e) { return e; } throw new Exception($"Expected {typeof(T).Name}"); }
    sealed record Request(HttpMethod Method, Uri Uri, string? Authorization, string? IfMatch, string? Body);
    sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.IfMatch.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request, Requests.Count);
        }
    }
    sealed class Credential : TokenCredential
    {
        public List<string> Scopes { get; } = [];
        public Exception? Failure { get; init; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) => GetTokenAsync(context, cancellationToken).GetAwaiter().GetResult();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Scopes.AddRange(context.Scopes);
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(new AccessToken("local-test-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
