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
    /// nothing to close out). Otherwise resolves a broker quote for the exit price, computes
    /// P&L, and closes both trade_metrics and the CSV log. A failed or timed out quote lookup
    /// still closes trade_metrics with null exit data rather than blocking the caller.
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

    private async Task<decimal?> TryGetExitPriceAsync(
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
