namespace Vela.Worker.Data;

/// <summary>
/// Persisted snapshot of an open trade position.
/// Written when TradeGuard registers an open, deleted when the position closes.
/// Allows TradeGuard to reload its in-memory state after a Worker restart
/// so exit alerts and position monitoring are not lost between sessions.
/// </summary>
public class OpenPosition
{
    public string OrderId { get; set; } = string.Empty;
    public string? StopOrderId { get; set; }
    public string? TargetOrderId { get; set; }
    public string AlertId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string TradeType { get; set; } = string.Empty;
    public string? OptionsContract { get; set; }
    public string? Direction { get; set; }
    public decimal? Strike { get; set; }
    public string? Expiration { get; set; }
    public int Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal EntryAmount { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TargetPrice { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public bool IsAverage { get; set; }
    public bool HasAveraged { get; set; }
    public bool IsManual { get; set; }

    /// <summary>
    /// Set when a close attempt was deferred because the broker rejected it or was
    /// unreachable (e.g. IB Gateway down). Holds the TradeOutcome to retry with.
    /// Cleared implicitly, the row is deleted once a retried close actually succeeds.
    /// </summary>
    public string? PendingCloseOutcome { get; set; }

    /// <summary>Timestamp of the first deferred close attempt, preserved across retries.</summary>
    public DateTimeOffset? PendingCloseSince { get; set; }

    /// <summary>
    /// Timestamp of the last periodic reconciliation cycle that confirmed this position still
    /// existed at the broker. Null until the first successful liveness check. Gives
    /// GhostPositionCloseOutService a bounded anchor, "confirmed open as of X" — to search
    /// intraday bars from when the position later turns up gone, instead of guessing across the
    /// unbounded range back to OpenedAt.
    /// </summary>
    public DateTimeOffset? LastVerifiedOpenAt { get; set; }
}