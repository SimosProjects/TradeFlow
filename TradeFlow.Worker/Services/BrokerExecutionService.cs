using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

// Shared broker execution logic called by both AlertPollingService and SignalRListenerService.
// Handles order placement for approved BTO entries and position closing for STC/BTC exits.
public class BrokerExecutionService
{
    private readonly IBrokerService          _broker;
    private readonly PositionSizer           _sizer;
    private readonly TradeGuard              _guard;
    private readonly CsvTradeLogger          _csv;
    private readonly DiscordNotificationService _discord;
    private readonly ILogger<BrokerExecutionService> _logger;

    public BrokerExecutionService(
        IBrokerService               broker,
        PositionSizer                sizer,
        TradeGuard                   guard,
        CsvTradeLogger               csv,
        DiscordNotificationService   discord,
        ILogger<BrokerExecutionService> logger)
    {
        _broker  = broker;
        _sizer   = sizer;
        _guard   = guard;
        _csv     = csv;
        _discord = discord;
        _logger  = logger;
    }

    // Called after risk engine approves a BTO entry alert
    public async Task HandleEntryAsync(
        Alert alert,
        AlertClassification classification,
        bool isAverage = false,
        CancellationToken ct = default)
    {
        // Never place orders outside regular market hours
        if (!IsMarketOpen())
        {
            _logger.LogDebug(
                "Market closed — skipping order for {Symbol}", alert.Symbol);
            return;
        }

        var order = _sizer.Size(alert, classification, isAverage);
        if (order is null)
        {
            _logger.LogWarning(
                "PositionSizer returned null for {Symbol} — price may be missing or quantity < 1",
                alert.Symbol);
            return;
        }

        // Run all safety checks before placing any order
        var blocked = await _guard.CheckAsync(order, ct);
        if (blocked is not null)
        {
            _logger.LogWarning(
                "TradeGuard blocked order for {Symbol} — {Reason}",
                alert.Symbol, blocked);
            return;
        }

        BrokerOrderResult result;
        try
        {
            result = await _broker.PlaceOrderAsync(order, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Broker PlaceOrderAsync failed for {Symbol} — skipping", alert.Symbol);
            return;
        }

        if (result.Status == OrderStatus.Rejected || result.Status == OrderStatus.Cancelled)
        {
            _logger.LogWarning(
                "Broker rejected order for {Symbol} — status: {Status}", alert.Symbol, result.Status);
            return;
        }

        // Register position in TradeGuard memory and write to CSV
        _guard.RegisterOpen(order, result);
        var trade = _guard.FindOpenTrade(
            order.UserName, order.OptionsContractSymbol, order.Symbol)!;

        await _csv.OpenTradeAsync(trade, ct);

        _logger.LogInformation(
            "ORDER PLACED — {Type} {Symbol} {Direction} × {Qty} @ ${Price:F2} | " +
            "Stop: ${Stop:F2} | Target: ${Target:F2} | OrderId: {OrderId}",
            order.TradeType, order.Symbol, order.Direction ?? "—",
            result.FillQuantity, result.FillPrice,
            order.StopPrice, order.TargetPrice, result.OrderId);

        await _discord.NotifyOrderPlacedAsync(trade, ct);
    }

    // Called when a side:stc or side:btc exit alert arrives from the same trader + contract
    public async Task HandleExitAsync(
        Alert alert,
        CancellationToken ct = default)
    {
        var trade = _guard.FindOpenTrade(
            alert.UserName ?? "",
            alert.OptionsContractSymbol,
            alert.Symbol ?? "");

        if (trade is null)
        {
            _logger.LogDebug(
                "Exit alert for {Symbol} — no matching open position, skipping broker close",
                alert.Symbol);
            return;
        }

        // Use the exit price from the alert if available, otherwise use last known price
        var exitPrice = alert.PriceAtExit ?? alert.LastCheckedPrice ?? trade.EntryPrice;

        BrokerOrderResult closeResult;
        try
        {
            closeResult = await _broker.ClosePositionAsync(trade, TradeOutcome.XtradesExit, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Broker ClosePositionAsync failed for {Symbol} — skipping", alert.Symbol);
            return;
        }

        var closedTrade = _guard.RegisterClose(
            alert.UserName ?? "",
            alert.OptionsContractSymbol,
            alert.Symbol ?? "",
            closeResult.FillPrice,
            TradeOutcome.XtradesExit);

        if (closedTrade is null) return;

        await _csv.CloseTradeAsync(closedTrade, ct);

        _logger.LogInformation(
            "POSITION CLOSED — {Symbol} × {Qty} @ ${Price:F2} | " +
            "P&L: {PnL:+$#,##0.00;-$#,##0.00} ({PnLPct:+0.00;-0.00}%) | Outcome: {Outcome}",
            closedTrade.Symbol, closedTrade.Quantity, closeResult.FillPrice,
            closedTrade.PnL ?? 0, closedTrade.PnLPercent ?? 0, closedTrade.Result);

        await _discord.NotifyPositionClosedAsync(closedTrade, ct);
    }

    // Returns true if current time is within regular market hours (9:30am-4:00pm ET, Mon-Fri)
    private static bool IsMarketOpen()
    {
        var et    = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var timeOfDay = et.TimeOfDay;
        return timeOfDay >= new TimeSpan(9, 30, 0)
            && timeOfDay <  new TimeSpan(16, 0, 0);
    }
}