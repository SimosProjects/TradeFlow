namespace Vela.Worker.Data;

/// <summary>
/// Persisted record of a reconciliation mismatch detected between Vela's tracked state
/// and IBKR's actual account state, or between the CSV trade log and trade_metrics.
/// Written by StartupReconciliationService, PeriodicReconciliationService, Vela.Guardian,
/// and csv_reconcile.py so mismatches are queryable for analytics instead of only
/// appearing in the log stream and Discord.
/// </summary>
public class ReconciliationEvent
{
    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public string? OrderId { get; set; }
    public string? Detail { get; set; }
}
