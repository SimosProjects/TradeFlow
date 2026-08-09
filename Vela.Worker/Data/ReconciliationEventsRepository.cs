namespace Vela.Worker.Data;

/// <summary>
/// Persists reconciliation mismatches detected against IBKR or the CSV trade log.
/// Write-only from the Worker's perspective, reads are done by Vela.Analytics directly.
/// </summary>
public interface IReconciliationEventsRepository
{
    /// <summary>Writes a new reconciliation event row.</summary>
    Task SaveAsync(ReconciliationEvent reconciliationEvent, CancellationToken ct = default);
}

/// <inheritdoc/>
public class ReconciliationEventsRepository : IReconciliationEventsRepository
{
    private readonly VelaDbContext _db;
    private readonly ILogger<ReconciliationEventsRepository> _logger;

    public ReconciliationEventsRepository(VelaDbContext db, ILogger<ReconciliationEventsRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ReconciliationEvent reconciliationEvent, CancellationToken ct = default)
    {
        try
        {
            _db.ReconciliationEvents.Add(reconciliationEvent);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to save reconciliation event for {Symbol}", reconciliationEvent.Symbol);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }
}
