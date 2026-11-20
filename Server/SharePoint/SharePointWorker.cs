using System.Text.Json;
using PdfCustode.Server.Audit;

namespace PdfCustode.Server.SharePoint;

public sealed class SharePointWorker(SharePointSynchronizer sync, SharePointOptions options, AuditJournal journal, ILogger<SharePointWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool failed = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(options.SyncIntervalSeconds);
            try
            {
                await sync.SynchronizeAsync(stoppingToken);
                if (failed) Record("Successo", "Sincronizzazione SharePoint ripristinata", "SharePointRecovered", null);
                failed = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                if (error is GraphApiException { RetryAfter: { } requested } && requested > delay) delay = requested;
                var graphError = error as GraphApiException;
                logger.LogWarning("SharePoint sync pending; error type {ErrorType}, status {Status}, code {Code}",
                    error.GetType().Name, graphError is null ? null : (int?)graphError.StatusCode, graphError?.ErrorCode);
                if (!failed)
                    journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "ErroreTecnico", "Errore", "Sincronizzazione SharePoint sospesa",
                        Details: JsonSerializer.Serialize(new { reason = graphError?.ErrorCode ?? "SharePointUnavailable", errorType = error.GetType().Name,
                            statusCode = graphError is null ? null : (int?)graphError.StatusCode })));
                failed = true;
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private void Record(string result, string title, string reason, string? errorType) =>
        journal.Record(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "ErroreTecnico", result, title,
            Details: JsonSerializer.Serialize(new { reason, errorType })));
}
