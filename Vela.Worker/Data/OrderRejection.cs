namespace Vela.Worker.Data;

/// <summary>
/// Persisted record of an entry order that was rejected, cancelled, or failed to place
/// at the broker. Written from BrokerExecutionService.ExecuteBrokerEntryAsync so rejected
/// orders are queryable for analytics instead of only appearing in the log stream.
/// </summary>
public class OrderRejection
{
    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? AlertId { get; set; }
    public string? TraderName { get; set; }
    public string? Symbol { get; set; }
    public string? TradeType { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int RequestedQuantity { get; set; }
    public decimal? RequestedPrice { get; set; }
}
