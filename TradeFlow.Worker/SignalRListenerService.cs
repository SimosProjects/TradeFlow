using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Channels;

namespace TradeFlow.Worker;

/// <summary>
/// Connects to the Xtrades Azure SignalR feed as a client and receives
/// live trading alerts in real time. Decouples the SignalR callback
/// from the processing pipeline using a bounded Channel to handle
/// burst traffic without blocking the connection.
/// </summary>
public class SignalRListenerService : BackgroundService
{
    private const string HubUrl = "https://xtrades-core-prod.service.signalr.net/client";

    private const string AlertEventName = "AlertReceived";

    private readonly IAlertNormalizer _normalizer;
    private readonly RiskEngineService _riskEngine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignalRListenerService> _logger;
    private readonly string _token;

    // Bounded channel, decouples SignalR callback (fast) from
    // processing pipeline (slower database operations)
    // 500 capacity handles burst traffic without blocking the connection
    private readonly Channel<object> _alertChannel =
        Channel.CreateBounded<object>(new BoundedChannelOptions(500)
        {
            // Block the writer when full. Better to slow the connection than to drop alerts
            FullMode = BoundedChannelFullMode.Wait
        });

    public SignalRListenerService(
        IAlertNormalizer                 normalizer,
        RiskEngineService                riskEngine,
        IServiceScopeFactory             scopeFactory,
        IOptions<XtradesOptions>         options,
        ILogger<SignalRListenerService>  logger)
    {
        _normalizer   = normalizer;
        _riskEngine   = riskEngine;
        _scopeFactory = scopeFactory;
        _logger       = logger;

        _token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
            ?? throw new InvalidOperationException(
                "XTRADES_TOKEN environment variable is not set.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalR listener service started.");

        // Run the SignalR connection loop and the processing loop concurrently
        // Both are cancelled by stoppingToken on shutdown
        await Task.WhenAll(
            RunConnectionLoopAsync(stoppingToken),
            RunProcessingLoopAsync(stoppingToken)
        );

        _logger.LogInformation("SignalR listener service stopped.");
    }

    /// <summary>
    /// Maintains the SignalR connection: connects, handles incoming messages,
    /// and reconnects with exponential backoff when the connection drops.
    /// </summary>
    private async Task RunConnectionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var connection = BuildConnection();

            // Register handler before connecting so no messages are missed
            connection.On<object>(AlertEventName, async rawAlert =>
            {
                _logger.LogDebug(
                    "SignalR alert received: {Raw}", rawAlert);

                try
                {
                    // Write to channel immediately — never block the callback
                    await _alertChannel.Writer.WriteAsync(
                        rawAlert, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown — expected
                }
            });

            // Log connection state transitions for monitoring
            connection.Reconnecting += ex =>
            {
                _logger.LogWarning(
                    "SignalR connection lost — reconnecting. Reason: {Reason}",
                    ex?.Message);
                return Task.CompletedTask;
            };

            connection.Reconnected += connectionId =>
            {
                _logger.LogInformation(
                    "SignalR reconnected. ConnectionId: {Id}", connectionId);
                return Task.CompletedTask;
            };

            connection.Closed += ex =>
            {
                _logger.LogWarning(
                    "SignalR connection closed. Reason: {Reason}",
                    ex?.Message ?? "clean close");
                return Task.CompletedTask;
            };

            try
            {
                await connection.StartAsync(stoppingToken);

                _logger.LogInformation(
                    "SignalR connected. ConnectionId: {Id}",
                    connection.ConnectionId);

                // Wait until the connection closes
                // HubConnection doesn't have a built-in "wait for close" method
                // so we poll the state
                while (connection.State != HubConnectionState.Disconnected
                       && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested — exit the loop
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SignalR connection failed — will retry with backoff.");
            }
            finally
            {
                await connection.DisposeAsync();
            }

            if (stoppingToken.IsCancellationRequested) break;

            // Exponential backoff before reconnecting
            // WithAutomaticReconnect handles transient drops
            // This outer loop handles complete connection failures
            await RetryWithBackoffAsync(stoppingToken);
        }

        // Signal the processing loop that no more alerts are coming
        _alertChannel.Writer.Complete();
    }

    /// <summary>
    /// Reads alerts from the channel and runs them through the pipeline.
    /// Runs concurrently with the connection loop, decoupled via Channel.
    /// </summary>
    private async Task RunProcessingLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var rawAlert in
            _alertChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessRawAlertAsync(rawAlert, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process SignalR alert — continuing.");
            }
        }
    }

    /// <summary>
    /// Parses and processes a raw alert from the SignalR feed through
    /// the normalize → classify → risk evaluate → persist pipeline.
    /// </summary>
    private async Task ProcessRawAlertAsync(
        object rawAlert,
        CancellationToken stoppingToken)
    {
        // TODO: deserialize rawAlert to Alert record once message format confirmed
        // For now log the raw payload so we can inspect the actual format
        _logger.LogInformation(
            "Processing SignalR alert: {Alert}", rawAlert);

        // Placeholder — real implementation deserializes and runs the pipeline:
        // var alert = JsonSerializer.Deserialize<Alert>(rawAlert.ToString()!);
        // if (alert is null || !_normalizer.IsProcessable(alert)) return;
        // var normalized   = _normalizer.Normalize(alert);
        // var classification = AlertClassifier.Classify(normalized);
        // var riskResult   = _riskEngine.Evaluate(normalized);
        // using var scope  = _scopeFactory.CreateScope();
        // var repository   = scope.ServiceProvider
        //                        .GetRequiredService<IAlertRepository>();
        // var entity       = AlertMapper.ToEntity(normalized, riskResult);
        // await repository.SaveManyAsync([entity], stoppingToken);
    }

    /// <summary>
    /// Builds a new HubConnection with JWT auth and automatic reconnect.
    /// A new connection is built each time the outer loop restarts.
    /// </summary>
    private HubConnection BuildConnection() =>
        new HubConnectionBuilder()
            .WithUrl(HubUrl, options =>
            {
                // Bearer JWT auth, same token used for REST API
                options.AccessTokenProvider =
                    () => Task.FromResult<string?>(_token);
            })
            .WithAutomaticReconnect(new ExponentialBackoffRetryPolicy())
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

    /// <summary>
    /// Waits with exponential backoff before the outer connection loop retries.
    /// This handles complete connection failures that WithAutomaticReconnect
    /// could not recover from.
    /// </summary>
    private async Task RetryWithBackoffAsync(
        CancellationToken stoppingToken,
        int attempt = 0)
    {
        var maxDelay    = TimeSpan.FromSeconds(60);
        var baseSeconds = Math.Pow(2, Math.Min(attempt, 6)); // cap exponent at 6
        var jitter      = Random.Shared.NextDouble() * 0.3;  // 0–30% jitter
        var delay       = TimeSpan.FromSeconds(baseSeconds * (1 + jitter));

        if (delay > maxDelay) delay = maxDelay;

        _logger.LogInformation(
            "Waiting {Delay:F1}s before reconnecting...", delay.TotalSeconds);

        await Task.Delay(delay, stoppingToken);
    }
}

/// <summary>
/// Exponential backoff with jitter for SignalR automatic reconnect.
/// Used for transient drops, the outer loop handles complete failures.
/// </summary>
public class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // Stop automatic reconnect after 5 attempts
        if (retryContext.PreviousRetryCount >= 5)
            return null;

        var baseSeconds = Math.Pow(2, retryContext.PreviousRetryCount);
        var jitter      = Random.Shared.NextDouble() * 0.3; // 0-30% jitter
        var delay       = TimeSpan.FromSeconds(baseSeconds * (1 + jitter));

        return delay < MaxDelay ? delay : MaxDelay;
    }
}