using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;

namespace TradeFlow.Worker;

/// <summary>
/// Connects to the Xtrades Azure SignalR feed as a client and receives
/// live trading alerts in real time. Uses a two-step connection flow:
/// 1. POST /api/v2/signalr/negotiate to get a short-lived SignalR token
/// 2. Connect to Azure SignalR using that token
/// Decouples the SignalR callback from the processing pipeline using
/// a bounded Channel to handle burst traffic without blocking the connection.
/// </summary>
public class SignalRListenerService : BackgroundService
{
    private const string NegotiateUrl =
        "https://app.xtrades.net/api/v2/signalr/negotiate";

    // Confirmed from browser DevTools inspection
    private const string AlertEventName   = "newAlert";
    private const string HubName          = "notification";

    private readonly IAlertNormalizer             _normalizer;
    private readonly RiskEngineService            _riskEngine;
    private readonly IServiceScopeFactory         _scopeFactory;
    private readonly ILogger<SignalRListenerService> _logger;
    private readonly HttpClient                   _httpClient;
    private readonly string                       _token;

    // Bounded channel — decouples SignalR callback (fast) from
    // processing pipeline (slower database operations)
    private readonly Channel<JsonElement> _alertChannel =
        Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    public SignalRListenerService(
        IAlertNormalizer                 normalizer,
        RiskEngineService                riskEngine,
        IServiceScopeFactory             scopeFactory,
        ILogger<SignalRListenerService>  logger)
    {
        _normalizer   = normalizer;
        _riskEngine   = riskEngine;
        _scopeFactory = scopeFactory;
        _logger       = logger;

        _token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
            ?? throw new InvalidOperationException(
                "XTRADES_TOKEN environment variable is not set.");

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalR listener service started.");

        await Task.WhenAll(
            RunConnectionLoopAsync(stoppingToken),
            RunProcessingLoopAsync(stoppingToken)
        );

        _logger.LogInformation("SignalR listener service stopped.");
    }

    /// <summary>
    /// Step 1: POST to Xtrades negotiate endpoint to get a short-lived
    /// Azure SignalR access token. Step 2: Connect to Azure SignalR using
    /// that token. Reconnects with exponential backoff on failure.
    /// </summary>
    private async Task RunConnectionLoopAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            HubConnection? connection = null;
            try
            {
                // Step 1 — negotiate to get SignalR endpoint + token
                var (hubUrl, signalRToken) = await NegotiateAsync(stoppingToken);

                // Step 2 — build connection with the short-lived token
                connection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.AccessTokenProvider =
                            () => Task.FromResult<string?>(signalRToken);
                    })
                    .WithAutomaticReconnect(new ExponentialBackoffRetryPolicy())
                    .ConfigureLogging(logging =>
                        logging.SetMinimumLevel(LogLevel.Warning))
                    .Build();

                // Register for the confirmed alert event name
                connection.On<JsonElement>(AlertEventName, async alert =>
                {
                    _logger.LogDebug("SignalR newAlert received");
                    try
                    {
                        await _alertChannel.Writer.WriteAsync(
                            alert, stoppingToken);
                    }
                    catch (OperationCanceledException) { }
                });

                connection.Reconnecting += ex =>
                {
                    _logger.LogWarning(
                        "SignalR reconnecting. Reason: {Reason}", ex?.Message);
                    return Task.CompletedTask;
                };

                connection.Reconnected += _ =>
                {
                    attempt = 0; // reset backoff on successful reconnect
                    _logger.LogInformation("SignalR reconnected.");
                    return Task.CompletedTask;
                };

                connection.Closed += ex =>
                {
                    _logger.LogWarning(
                        "SignalR closed. Reason: {Reason}",
                        ex?.Message ?? "clean close");
                    return Task.CompletedTask;
                };

                await connection.StartAsync(stoppingToken);
                attempt = 0; // reset on successful connection

                _logger.LogInformation(
                    "SignalR connected. Hub: {Hub}, ConnectionId: {Id}",
                    HubName, connection.ConnectionId);

                // Wait until disconnected or cancelled
                while (connection.State != HubConnectionState.Disconnected
                       && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SignalR connection failed — will retry with backoff.");
            }
            finally
            {
                if (connection is not null)
                    await connection.DisposeAsync();
            }

            if (stoppingToken.IsCancellationRequested) break;

            await RetryWithBackoffAsync(attempt++, stoppingToken);
        }

        _alertChannel.Writer.Complete();
    }

    /// <summary>
    /// Calls the Xtrades negotiate endpoint to get a short-lived Azure
    /// SignalR connection URL and access token.
    /// </summary>
    private async Task<(string HubUrl, string Token)> NegotiateAsync(
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            NegotiateUrl, null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"SignalR negotiate failed: HTTP {(int)response.StatusCode} — {body}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Response contains the Azure SignalR URL and a short-lived token
        var url   = root.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Negotiate response missing 'url'");
        var token = root.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("Negotiate response missing 'accessToken'");

        _logger.LogInformation("SignalR negotiate succeeded. Hub URL: {Url}", url);

        return (url, token);
    }

    /// <summary>
    /// Reads alerts from the channel and runs them through the pipeline.
    /// </summary>
    private async Task RunProcessingLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var alertElement in
            _alertChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAlertAsync(alertElement, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process SignalR alert — continuing.");
            }
        }
    }

    /// <summary>
    /// Deserializes and processes a newAlert event through the full pipeline:
    /// normalize → classify → risk evaluate → deduplicate → persist.
    /// </summary>
    private async Task ProcessAlertAsync(
        JsonElement alertElement,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Processing SignalR alert: {Raw}", alertElement.GetRawText());

        // Payload arrives as a JSON array, take the first element
        var element = alertElement.ValueKind == JsonValueKind.Array
            ? alertElement[0]
            : alertElement;

        // Deserialize to Alert record
        var alert = JsonSerializer.Deserialize<Alert>(
            element.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (alert is null)
        {
            _logger.LogWarning("SignalR alert deserialized to null — skipping.");
            return;
        }

        if (!_normalizer.IsProcessable(alert))
        {
            _logger.LogDebug(
                "SignalR alert not processable (missing required fields) — skipping.");
            return;
        }

        var normalized     = _normalizer.Normalize(alert);
        var classification = AlertClassifier.Classify(normalized);
        var riskResult     = _riskEngine.Evaluate(normalized);

        _logger.LogInformation(
            "SignalR alert [{Category}] {Symbol} by {Trader} — {Result}",
            classification.Category,
            normalized.Symbol,
            normalized.UserName,
            riskResult.Approved ? "APPROVED" : $"REJECTED: {riskResult.Reason}");

        using var scope = _scopeFactory.CreateScope();
        var repository  = scope.ServiceProvider
                               .GetRequiredService<IAlertRepository>();

        var entity = AlertMapper.ToEntity(normalized, riskResult);

        var existingIds = await repository.GetExistingAlertIdsAsync(
            [entity.Id], stoppingToken);

        if (existingIds.Contains(entity.Id))
        {
            _logger.LogDebug(
                "SignalR alert {Id} already exists — skipping.", entity.Id);
            return;
        }

        await repository.SaveManyAsync([entity], stoppingToken);
    }

    private async Task RetryWithBackoffAsync(
        int attempt, CancellationToken stoppingToken)
    {
        var maxDelay    = TimeSpan.FromSeconds(60);
        var baseSeconds = Math.Pow(2, Math.Min(attempt, 6));
        var jitter      = Random.Shared.NextDouble() * 0.3;
        var delay       = TimeSpan.FromSeconds(baseSeconds * (1 + jitter));

        if (delay > maxDelay) delay = maxDelay;

        _logger.LogInformation(
            "Waiting {Delay:F1}s before reconnecting...", delay.TotalSeconds);

        await Task.Delay(delay, stoppingToken);
    }
}

/// <summary>
/// Exponential backoff with jitter for SignalR automatic reconnect.
/// Used for transient drops — the outer loop handles complete failures.
/// </summary>
public class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.PreviousRetryCount >= 5)
            return null;

        var baseSeconds = Math.Pow(2, retryContext.PreviousRetryCount);
        var jitter      = Random.Shared.NextDouble() * 0.3;
        var delay       = TimeSpan.FromSeconds(baseSeconds * (1 + jitter));

        return delay < MaxDelay ? delay : MaxDelay;
    }
}