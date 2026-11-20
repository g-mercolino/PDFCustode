using System.Diagnostics;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PdfCustode.Core;
using PdfCustode.Server;
using PdfCustode.Server.Audit;
using PdfCustode.Server.SharePoint;

bool check = args.Contains("--sharepoint-check");
bool synchronize = args.Contains("--sharepoint-sync");
var builder = WebApplication.CreateBuilder(args.Where(a => a is not "--sharepoint-check" and not "--sharepoint-sync").ToArray());
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Azure", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8192);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5187");
string folder = Environment.GetEnvironmentVariable("PDFCUSTODE_DATA") ?? Path.Combine(builder.Environment.ContentRootPath, "data");
if (!Directory.Exists(folder))
    throw new InvalidOperationException("Impostare PDFCUSTODE_DATA sulla cartella Admin o su una cartella server generata dal preparatore.");
builder.Services.AddSingleton(new ActivationCatalog(folder));
var sharePoint = builder.Configuration.GetSection("SharePoint").Get<SharePointOptions>() ?? new();
sharePoint.Validate();
builder.Services.AddSingleton(sharePoint);
builder.Services.AddSingleton(new AuditJournal(Path.Combine(folder, ".audit")));
builder.Services.AddSingleton<ActivationCoordinator>(services => new(services.GetRequiredService<ActivationCatalog>(),
    sharePoint.Enabled ? services.GetRequiredService<SharePointSynchronizer>() : null));
if (sharePoint.Enabled)
{
    builder.Services.AddSingleton<IGraphLists>(_ => new GraphListsClient(GraphAuthentication.CreateHttpClient(),
        GraphAuthentication.CreateCredential(sharePoint), sharePoint));
    builder.Services.AddSingleton<SharePointSynchronizer>();
    builder.Services.AddHostedService<SharePointWorker>();
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Troppi tentativi. Riprovare tra un minuto." }, token);
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
var journal = app.Services.GetRequiredService<AuditJournal>();
if (check || synchronize)
{
    if (!sharePoint.Enabled) throw new InvalidOperationException("Abilitare SharePoint nella configurazione locale.");
    var sync = app.Services.GetRequiredService<SharePointSynchronizer>();
    await sync.ValidateAsync(CancellationToken.None);
    Console.WriteLine("Autenticazione e schema delle tre liste verificati.");
    if (synchronize)
    {
        await sync.SynchronizeAsync(CancellationToken.None);
        Console.WriteLine("Sincronizzazione completata.");
    }
    return;
}
journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "ErroreTecnico", "Successo", "Avvio servizio",
    Details: JsonSerializer.Serialize(new { reason = "ServerStarted", configuredDocuments = sharePoint.DocumentIds.Count })));
app.Lifetime.ApplicationStopping.Register(() =>
    journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "ErroreTecnico", "Successo", "Arresto servizio",
        Details: JsonSerializer.Serialize(new { reason = "ServerStopping" }))));
app.Use(async (context, next) =>
{
    string requestId = Guid.NewGuid().ToString("N");
    context.Items["AuditRequestId"] = requestId;
    context.Response.Headers["X-Request-Id"] = requestId;
    bool activation = context.Request.Path == "/api/activate";
    string route = activation ? "/api/activate" : "Other";
    journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, activation ? "Attivazione" : "ErroreTecnico", "Successo",
        "Richiesta ricevuta", RequestId: requestId,
        Details: JsonSerializer.Serialize(new { reason = "RequestReceived", method = context.Request.Method, path = route })));
    long started = Stopwatch.GetTimestamp();
    bool failed = false;
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        failed = true;
        if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "ErroreTecnico", "Errore", "Richiesta interrotta",
            RequestId: requestId, Details: JsonSerializer.Serialize(new { reason = "RequestCancelled" })));
    }
    catch (Exception error)
    {
        failed = true;
        var graphError = error as GraphApiException;
        app.Logger.LogError("Request failed; request {RequestId}, type {ErrorType}, remote status {Status}, code {Code}",
            requestId, error.GetType().Name, graphError is null ? null : (int?)graphError.StatusCode, graphError?.ErrorCode);
        journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "ErroreTecnico", "Errore", "Errore richiesta",
            RequestId: requestId, Details: JsonSerializer.Serialize(new { reason = graphError?.ErrorCode ?? "UnhandledFailure", errorType = error.GetType().Name,
                statusCode = graphError is null ? null : (int?)graphError.StatusCode })));
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "Servizio temporaneamente non disponibile.", requestId });
        }
    }
    finally
    {
        journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, activation ? "Attivazione" : "ErroreTecnico",
            failed ? "Errore" : context.Response.StatusCode < 400 ? "Successo" : "Rifiutato", "Richiesta terminata", RequestId: requestId,
            Details: JsonSerializer.Serialize(new { reason = "RequestCompleted", statusCode = context.Response.StatusCode,
                elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds })));
    }
});
app.UseRateLimiter();
app.MapPost("/api/activate", async (HttpContext context, ActivationCoordinator coordinator) =>
{
    string requestId = (string)context.Items["AuditRequestId"]!;
    try
    {
        if (!context.Request.HasJsonContentType())
            return Results.Json(new { error = "Usare Content-Type application/json.", requestId }, statusCode: 415);
        var request = await context.Request.ReadFromJsonAsync<ActivationRequest>(context.RequestAborted);
        if (request is null) return Results.BadRequest(new { error = "Richiesta di attivazione non valida." });
        var license = await coordinator.ActivateAsync(request, context.RequestAborted);
        var payload = license is null ? null : JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(license.Payload));
        journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "Attivazione", license is null ? "Rifiutato" : "Successo",
            license is null ? "Attivazione rifiutata" : "Licenza restituita", DocumentId: ValidDocumentId(request.DocumentId),
            LicenseId: payload?.LicenseId, RequestId: requestId,
            Details: JsonSerializer.Serialize(new { reason = license is null ? "InvalidCodeDocumentOrDevice" : "LicenseReturned" })));
        return license is null
            ? Results.Json(new { error = "Attivazione rifiutata. Verificare il codice e il dispositivo." }, statusCode: 403)
            : Results.Ok(license);
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Json(new { error = "Richiesta di attivazione non valida." }, statusCode: ex.StatusCode);
    }
    catch (LicenseQuotaExceededException)
    {
        journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "Attivazione", "Rifiutato", "Licenze esaurite",
            RequestId: requestId, Details: JsonSerializer.Serialize(new { reason = "QuotaExceeded" })));
        return Results.Json(new { error = "Licenze disponibili esaurite.", requestId }, statusCode: 403);
    }
    catch (ActivationProfileValidationException error)
    {
        journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "Attivazione", "Rifiutato", "Profilo di attivazione non valido",
            RequestId: requestId, Details: JsonSerializer.Serialize(new { reason = "InvalidActivationProfile" })));
        return Results.BadRequest(new { error = error.Message, requestId });
    }
    catch (Exception ex) when (ex is JsonException)
    {
        return Results.BadRequest(new { error = "Inviare una richiesta di attivazione JSON valida." });
    }
});
app.Run();

static string? ValidDocumentId(string? id) => id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
