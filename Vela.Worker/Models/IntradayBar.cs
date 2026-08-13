namespace Vela.Worker.Models;

/// <summary>
/// A single timestamped OHLCV bar returned by IbkrBrokerService.GetIntradayBarsAsync.
/// Unlike HistoricalBar (daily, date-only), this carries a full timestamp so a caller can
/// select the bar closest to a specific moment within a trading day.
/// </summary>
public record IntradayBar(
    DateTimeOffset Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume
);
