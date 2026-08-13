using Vela.Worker.Data;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// Closes out the trade_metrics row and CSV trade log entry for a position discovered
/// gone at the broker during reconciliation. Shared by StartupReconciliationService and
/// PeriodicReconciliationService so the same close-out sequence runs regardless of which
/// detection path (short covering, DB verification, or periodic ghost cleanup) finds it,
/// always called immediately before the caller deletes the row from open_positions.
/// </summary>
public class GhostPositionCloseOutService
{
    private readonly IBrokerService _broker;
    private readonly ITradeMetricsRepository _metrics;
    private readonly CsvTradeLogger _csv;
    private readonly ILogger<GhostPositionCloseOutService> _logger;

    public GhostPositionCloseOutService(
        IBrokerService broker,
        ITradeMetricsRepository metrics,
        CsvTradeLogger csv,
        ILogger<GhostPositionCloseOutService> logger)
    {
        _broker  = broker;
        _metrics = metrics;
        _csv     = csv;
        _logger  = logger;
    }

    /// <summary>
    /// No-ops if no trade_metrics row exists for this OrderId (manual and reconciliation
    /// synthesized positions were never opened through the normal entry flow, so there is
    /// nothing to close out). Otherwise resolves an exit price via a three tier waterfall
    /// (see TryGetExitPriceAsync), computes P&L, and closes both trade_metrics and the CSV log.
    /// A failure at every tier still closes trade_metrics with null exit data rather than
    /// blocking the caller.
    /// </summary>
    public async Task CloseOutAsync(OpenPosition position, CancellationToken ct = default)
    {
        var metric = await _metrics.GetByOrderIdAsync(position.OrderId, ct);
        if (metric is null) return;

        var tradeType = position.TradeType == "Options" ? TradeType.Options : TradeType.Stock;
        var exitPrice = await TryGetExitPriceAsync(position, tradeType, ct);

        decimal? exitAmount = null;
        decimal? pnl = null;
        decimal? pnlPct = null;

        if (exitPrice.HasValue)
        {
            var multiplier = tradeType == TradeType.Options ? 100m : 1m;
            exitAmount = exitPrice.Value * position.Quantity * multiplier;
            pnl        = exitAmount.Value - position.EntryAmount;
            pnlPct     = position.EntryAmount != 0m ? pnl.Value / position.EntryAmount * 100m : 0m;
        }

        var closedAt = DateTimeOffset.UtcNow;

        await _metrics.CloseAsync(
            orderId:         position.OrderId,
            exitPrice:       exitPrice,
            exitAmount:      exitAmount,
            pnl:             pnl,
            pnlPct:          pnlPct,
            outcome:         TradeOutcome.ClosedExternally.ToString(),
            closedAt:        closedAt,
            exitLatencyMs:   null,
            exitSlippagePct: null,
            ct:              ct);

        var tradeRecord = new TradeRecord
        {
            AlertId         = position.AlertId,
            OrderId         = position.OrderId,
            StopOrderId     = position.StopOrderId,
            TargetOrderId   = position.TargetOrderId,
            UserName        = position.UserName,
            XScore          = metric.XScore ?? 0m,
            DiscordRank     = metric.DiscordRank,
            Symbol          = position.Symbol,
            TradeType       = tradeType,
            OptionsContract = position.OptionsContract,
            Direction       = position.Direction,
            Strike          = position.Strike,
            Expiration      = position.Expiration,
            Quantity        = position.Quantity,
            EntryPrice      = position.EntryPrice,
            EntryAmount     = position.EntryAmount,
            StopPrice       = position.StopPrice,
            TargetPrice     = position.TargetPrice,
            OpenedAt        = position.OpenedAt,
            ClosedAt        = closedAt,
            ExitPrice       = exitPrice,
            ExitAmount      = exitAmount,
            PnL             = pnl,
            PnLPercent      = pnlPct,
            Status          = TradeStatus.Closed,
            Result          = TradeOutcome.ClosedExternally,
        };

        await _csv.CloseTradeAsync(tradeRecord, ct);

        _logger.LogInformation(
            "Ghost position close-out complete — {Symbol} (OrderId {OrderId}) | Exit: {ExitPrice} | P&L: {PnL}",
            position.Symbol, position.OrderId,
            exitPrice?.ToString("F2") ?? "n/a", pnl?.ToString("F2") ?? "n/a");
    }

    // -- Helpers --

    // Three tier waterfall, most accurate first:
    //   1. reqExecutions — the actual fill, when IBKR still has it (current trading day only).
    //   2. Intraday bars anchored on LastVerifiedOpenAt — bounded approximation for a gap that
    //      crossed the reqExecutions retention window, only attempted when an anchor exists.
    //   3. Live quote — last resort, whatever the market is doing right now.
    // Any tier failing (exception, empty result) falls through to the next rather than blocking
    // the caller, only exhausting all three closes trade_metrics with null exit data.
    private async Task<decimal?> TryGetExitPriceAsync(
        OpenPosition position, TradeType tradeType, CancellationToken ct)
    {
        var executionPrice = await TryGetExecutionPriceAsync(position, tradeType, ct);
        if (executionPrice.HasValue) return executionPrice;

        var barPrice = await TryGetHistoricalBarPriceAsync(position, tradeType, ct);
        if (barPrice.HasValue) return barPrice;

        return await TryGetLiveQuotePriceAsync(position, tradeType, ct);
    }

    // Tier 1 — the actual fill, if IBKR still has it in the current trading day's execution
    // history. Covers a close that happened while this Worker process wasn't running or wasn't
    // watching that specific order, regardless of what placed it.
    private async Task<decimal?> TryGetExecutionPriceAsync(
        OpenPosition position, TradeType tradeType, CancellationToken ct)
    {
        try
        {
            var execution = await _broker.GetRecentExecutionAsync(
                position.Symbol, tradeType, position.OptionsContract, position.Direction,
                position.Strike, position.Expiration, ct);

            if (execution is null) return null;

            _logger.LogInformation(
                "Ghost position close-out — found actual execution for {Symbol} (OrderId {OrderId}): " +
                "${Price:F2} at {Time}.",
                position.Symbol, position.OrderId, execution.Price, execution.Time);
            return execution.Price;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Ghost position close-out — execution history lookup failed for {Symbol} (OrderId {OrderId}).",
                position.Symbol, position.OrderId);
            return null;
        }
    }

    // Tier 2 — approximates the exit from intraday bars bounded between LastVerifiedOpenAt (the
    // last reconciliation cycle that confirmed the position was still open) and now. Skips
    // entirely when no anchor exists (e.g. a row from before this column existed, or the very
    // first liveness check already missed), searching from OpenedAt instead would span hours or
    // days and be no more accurate than the live quote it exists to improve on.
    private async Task<decimal?> TryGetHistoricalBarPriceAsync(
        OpenPosition position, TradeType tradeType, CancellationToken ct)
    {
        if (position.LastVerifiedOpenAt is not { } anchor)
        {
            _logger.LogDebug(
                "Ghost position close-out — no LastVerifiedOpenAt anchor for {Symbol} (OrderId {OrderId}), " +
                "skipping historical bar tier.",
                position.Symbol, position.OrderId);
            return null;
        }

        try
        {
            var bars = await _broker.GetIntradayBarsAsync(
                position.Symbol, tradeType, position.Direction, position.Strike, position.Expiration,
                anchor, DateTimeOffset.UtcNow, ct);

            var lastBar = bars.OrderByDescending(b => b.Time).FirstOrDefault();
            if (lastBar is null)
            {
                _logger.LogWarning(
                    "Ghost position close-out — no intraday bars for {Symbol} (OrderId {OrderId}) " +
                    "between {Anchor} and now.",
                    position.Symbol, position.OrderId, anchor);
                return null;
            }

            _logger.LogInformation(
                "Ghost position close-out — approximating exit for {Symbol} (OrderId {OrderId}) from " +
                "intraday bar at {Time}: ${Price:F2}.",
                position.Symbol, position.OrderId, lastBar.Time, lastBar.Close);
            return lastBar.Close;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Ghost position close-out — intraday bar lookup failed for {Symbol} (OrderId {OrderId}).",
                position.Symbol, position.OrderId);
            return null;
        }
    }

    // Tier 3 — last resort, a fresh live quote with no relationship to the actual close moment.
    private async Task<decimal?> TryGetLiveQuotePriceAsync(
        OpenPosition position, TradeType tradeType, CancellationToken ct)
    {
        try
        {
            var quote = await _broker.GetCurrentMarketPriceAsync(
                position.Symbol, tradeType, position.Direction,
                position.Strike, position.Expiration, ct);

            if (quote > 0m) return quote;

            _logger.LogWarning(
                "Ghost position close-out — quote lookup returned no price for {Symbol} " +
                "(OrderId {OrderId}). Closing trade_metrics with null exit data.",
                position.Symbol, position.OrderId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Ghost position close-out — quote lookup failed for {Symbol} (OrderId {OrderId}). " +
                "Closing trade_metrics with null exit data.",
                position.Symbol, position.OrderId);
            return null;
        }
    }
}
