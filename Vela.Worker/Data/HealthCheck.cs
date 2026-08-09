namespace Vela.Worker.Data;

/// <summary>
/// Persisted snapshot of a scheduled system health check. Written by MarketSchedulerService
/// alongside the existing Discord health check embed so status history is queryable
/// instead of only existing in Discord.
/// </summary>
public class HealthCheck
{
    public int Id { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public string WorkerStatus { get; set; } = string.Empty;
    public string IbkrStatus { get; set; } = string.Empty;
    public string PostgresStatus { get; set; } = string.Empty;
    public string XtradesStatus { get; set; } = string.Empty;
    public string SignalrStatus { get; set; } = string.Empty;
}
