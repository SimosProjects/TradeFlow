namespace TradeFlow.Worker.Models;

/// <summary>
/// Result returned from the broker after placing an order.
/// </summary>
public record TradeResult(
    string      OrderId,           // broker order ID
    string?     StopOrderId,       // stop order ID
    string?     TargetOrderId,     // target order ID
    decimal     FillPrice,         // actual fill price
    int         FillQuantity,      // actual fill quantity
    decimal     FillAmount,        // FillPrice × FillQuantity × 100 (options) or × 1 (stocks)
    OrderStatus Status,
    DateTimeOffset FilledAt
);