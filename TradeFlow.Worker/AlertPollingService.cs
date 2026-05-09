namespace TradeFlow.Worker;

/// <summary>
/// Polls the XTrades API for new alerts on a regular interval, processes them through the
/// normalization, classification, and risk evaluation pipeline, and logs the results.
/// </summary>
public class AlertPollingService : BackgroundService
{
    private readonly IAlertApiClient _client;
    private readonly IAlertNormalizer _normalizer;
    private readonly RiskEngineService _riskEngine;
    private readonly ILogger<AlertPollingService> _logger;

    // Poll interval for checking new alerts
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    public AlertPollingService(
        IAlertApiClient client,
        IAlertNormalizer normalizer,
        RiskEngineService riskEngine,
        ILogger<AlertPollingService> logger)
    {
        _client = client;
        _normalizer = normalizer;
        _riskEngine = riskEngine;
        _logger = logger;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alert polling service started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);

                    await Task.Delay(PollingInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown — not an error
            }
            finally
            {
                // Guaranteed to run whether cancelled or not
                _logger.LogInformation("Alert polling service stopped.");
            }
    }

    /// <summary>
    /// Executes a single poll cycle: fetches alerts, processes them through the pipeline, and logs the results.
    /// Exceptions are caught and logged so the polling loop always continues.
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var alerts = await _client.GetAlertsAsync(stoppingToken);
            
            _logger.LogInformation("Fetched {Count} alerts from API.", alerts.Count);

            var processed = alerts
                .Where(_normalizer.IsProcessable)
                .Select(_normalizer.Normalize)
                .Select(alerts => (
                    Alert: alerts,
                    Classification: AlertClassifier.Classify(alerts),
                    RiskResult: _riskEngine.Evaluate(alerts)
                ))
                .ToList();

            var approved = processed.Where(p => p.RiskResult.Approved).ToList();
            var rejected = processed.Where(p => !p.RiskResult.Approved).ToList();

            _logger.LogInformation("Pipeline complete - Approved: {Approved}, Rejected: {Rejected}",
                approved.Count, rejected.Count);

            foreach (var (alert, classification, _) in approved)
            {
                _logger.LogInformation("APPROVED [{Category}] {Symbol} by {Trader} (xScore: {XScore})",
                    classification.Category,
                    alert.Symbol,
                    alert.UserName,
                    alert.XScore);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll cycle failed - will retry in {Interval}s.", PollingInterval.TotalSeconds);
        }
    }
}
