namespace Vela.Worker.Data;

/// <summary>
/// Persists scheduled system health check snapshots. Write-only from the Worker's
/// perspective, reads are done by Vela.Analytics directly.
/// </summary>
public interface IHealthChecksRepository
{
    /// <summary>Writes a new health check row.</summary>
    Task SaveAsync(HealthCheck healthCheck, CancellationToken ct = default);
}

/// <inheritdoc/>
public class HealthChecksRepository : IHealthChecksRepository
{
    private readonly VelaDbContext _db;
    private readonly ILogger<HealthChecksRepository> _logger;

    public HealthChecksRepository(VelaDbContext db, ILogger<HealthChecksRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(HealthCheck healthCheck, CancellationToken ct = default)
    {
        try
        {
            _db.HealthChecks.Add(healthCheck);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save health check");
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }
}
