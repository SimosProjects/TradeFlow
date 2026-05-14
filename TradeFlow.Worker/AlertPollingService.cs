using System.Diagnostics;
using TradeFlow.Worker.Metrics;

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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PollingOptions _options;
    private readonly ILogger<AlertPollingService> _logger;
    private readonly AlertMetrics _metrics;
    private readonly DiscordNotificationService _discord;

    public AlertPollingService(
        IAlertApiClient client,
        IAlertNormalizer normalizer,
        RiskEngineService riskEngine,
        IServiceScopeFactory scopeFactory,
        IOptions<PollingOptions> options,
        ILogger<AlertPollingService> logger,
        AlertMetrics metrics,
        DiscordNotificationService discord)
    {
        _client = client;
        _normalizer = normalizer;
        _riskEngine = riskEngine;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _discord = discord;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Alert polling service started - interval: {Interval}s",
            _options.IntervalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.IntervalSeconds),
                    stoppingToken);
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
        var sw = Stopwatch.StartNew();
        
        try
        {
            // Create a new scope for this poll cycle to get a fresh instance of the repository
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
    
            var alerts = await _client.GetAlertsAsync(stoppingToken);

            _metrics.AlertsFetched.Add(alerts.Count);

            _logger.LogInformation("Fetched {Count} alerts from API.", alerts.Count);

            // One query to the database to get all existing IDs, then filter in memory - more efficient than querying for each alert
            var incomingIds = alerts
                .Where(a => a.Id is not null)
                .Select(a => a.Id!)
                .ToList();
            
            var existingIds = await repository.GetExistingAlertIdsAsync(incomingIds, stoppingToken);

            // Filter out any alerts that already exist in the database based on their ID
            var newAlerts = alerts
                .Where(a => a.Id is not null && !existingIds.Contains(a.Id!))
                .ToList();

            _metrics.AlertsNew.Add(newAlerts.Count);

            _logger.LogInformation("New alerts after deduplication: {New} / {Total}", newAlerts.Count, alerts.Count);

            if (newAlerts.Count == 0)
            {
                return; // No new alerts to process
            }

            var processed = newAlerts
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

            _metrics.AlertsApproved.Add(approved.Count);
            _metrics.AlertsRejected.Add(rejected.Count);

            _logger.LogInformation("Pipeline complete - Approved: {Approved}, Rejected: {Rejected}",
                approved.Count, rejected.Count);

            foreach (var (alert, classification, _) in approved)
            {
                _logger.LogInformation("APPROVED [{Category}] {Symbol} by {Trader} (xScore: {XScore})",
                    classification.Category,
                    alert.Symbol,
                    alert.UserName,
                    alert.XScore);

                // Send Discord notification
                await _discord.NotifyApprovedAlertAsync(alert, classification, stoppingToken);
            }

            sw.Stop();
            _metrics.PollDurationMs.Record(sw.ElapsedMilliseconds,
                new TagList { { "result", "success" } });

            // Save both approved and rejected alerts with their risk evaluation so we can audit why alerts were filtered out
            var entities = processed.Select(p => AlertMapper.ToEntity(p.Alert, p.RiskResult)).ToList();
            await repository.SaveManyAsync(entities, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics.PollDurationMs.Record(sw.ElapsedMilliseconds,
                new TagList { { "result", "error" } });
                
            _logger.LogError(
                ex, 
                "Poll cycle failed - will retry in {Interval}s.", 
                _options.IntervalSeconds);
        }
    }
}
