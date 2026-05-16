using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Background service that polls open positions every 60 seconds and checks
/// whether the current price has hit the stop loss or profit target.
/// Runs independently of the alert pipeline so positions are monitored
/// even when no new alerts are arriving.
/// </summary>
public class PositionMonitorService : BackgroundService
{
    private readonly TradeGuard _guard;
    private readonly IBrokerService _broker;
    private readonly CsvTradeLogger _csv;
    private readonly DiscordNotificationService _discord;
    private readonly ILogger<PositionMonitorService> _logger;

    private const int PollIntervalSeconds = 60;

    public PositionMonitorService(
        TradeGuard guard,
        IBrokerService broker,
        CsvTradeLogger csv,
        DiscordNotificationService discord,
        ILogger<PositionMonitorService> logger)
    {
        _guard = guard;
        _broker = broker;
        _csv = csv;
        _discord = discord;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Position monitor service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), stoppingToken);

            try
            {
                await CheckOpenPositionsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Position monitor cycle failed, will retry next interval.");
            }
        }

        _logger.LogInformation("Position monitor service stopped.");
    }

    // Iterates all open positions and checks whether stop or target has been hit
    private async Task CheckOpenPositionsAsync(CancellationToken ct)
    {
        var openTrades = _guard.GetOpenTrades();

        if (openTrades.Count == 0)
            return;

        _logger.LogDebug(
            "Position monitor checking {Count} open position(s).", openTrades.Count);

        foreach (var trade in openTrades)
        {
            await CheckPositionAsync(trade, ct);
        }
    }

    private async Task CheckPositionAsync(TradeRecord trade, CancellationToken ct)
    {
        // Get current price from IBKR — for now we rely on the broker's position data
        // In a future iteration this could use reqMktData for real-time quotes
        var currentPrice = await GetCurrentPriceAsync(trade, ct);
        if (currentPrice <= 0)
            return;

        var outcome = EvaluatePosition(trade, currentPrice);
        if (outcome == TradeOutcome.Open)
            return;

        _logger.LogInformation(
            "Position monitor: {Symbol} hit {Outcome} at ${Price:F2}",
            trade.Symbol, outcome, currentPrice);

        await ClosePositionAsync(trade, outcome, currentPrice, ct);
    }

    // Compares current price against stop and target thresholds
    private static TradeOutcome EvaluatePosition(TradeRecord trade, decimal currentPrice)
    {
        if (currentPrice <= trade.StopPrice)
            return TradeOutcome.StoppedOut;

        if (currentPrice >= trade.TargetPrice)
            return TradeOutcome.TargetHit;

        return TradeOutcome.Open;
    }

    private async Task ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        decimal currentPrice,
        CancellationToken ct)
    {
        BrokerOrderResult closeResult;
        try
        {
            closeResult = await _broker.ClosePositionAsync(trade, outcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Position monitor failed to close {Symbol}, will retry next cycle.", trade.Symbol);
            return;
        }

        var closedTrade = _guard.RegisterClose(
            trade.AlertId,
            trade.OptionsContract,
            trade.Symbol,
            closeResult.FillPrice,
            outcome);

        if (closedTrade is null) return;

        await _csv.CloseTradeAsync(closedTrade, ct);

        _logger.LogInformation(
            "Position monitor closed {Symbol} | Outcome: {Outcome} | " +
            "P&L: {PnL:+$#,##0.00;-$#,##0.00} ({PnLPct:+0.00;-0.00}%)",
            closedTrade.Symbol, outcome,
            closedTrade.PnL ?? 0, closedTrade.PnLPercent ?? 0);

        await _discord.NotifyPositionClosedAsync(closedTrade, ct);
    }

    // Fetches the current market price for a position.
    // Uses IBKR account positions for now — a future iteration can use live market data.
    private async Task<decimal> GetCurrentPriceAsync(TradeRecord trade, CancellationToken ct)
    {
        try
        {
            return await _broker.GetCurrentPositionPriceAsync(trade, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get current price for {Symbol}.", trade.Symbol);
            return 0m;
        }
    }
}