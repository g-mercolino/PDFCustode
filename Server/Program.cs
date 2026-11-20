using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PdfCustode.Core;
using PdfCustode.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5187");
string folder = Environment.GetEnvironmentVariable("PDFCUSTODE_DATA") ?? Path.Combine(builder.Environment.ContentRootPath, "data");
if (!Directory.Exists(folder))
    throw new InvalidOperationException("Impostare PDFCUSTODE_DATA sulla cartella Admin o su una cartella server generata dal preparatore.");
builder.Services.AddSingleton(new ActivationCatalog(folder));
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
app.UseRateLimiter();
app.MapPost("/api/activate", async (HttpRequest http, ActivationCatalog store, CancellationToken token) =>
{
    try
    {
        var request = await http.ReadFromJsonAsync<ActivationRequest>(token);
        if (request is null) return Results.BadRequest(new { error = "Richiesta di attivazione non valida." });
        var license = store.Activate(request);
        return license is null
            ? Results.Json(new { error = "Attivazione rifiutata. Verificare il codice e il dispositivo." }, statusCode: 403)
            : Results.Ok(license);
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Json(new { error = "Richiesta di attivazione non valida." }, statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = "Inviare una richiesta di attivazione JSON valida." });
    }
});
app.Run();
