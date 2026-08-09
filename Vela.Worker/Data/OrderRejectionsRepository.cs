namespace Vela.Worker.Data;

/// <summary>
/// Persists rejected, cancelled, or failed entry order attempts for analytics.
/// Write-only from the Worker's perspective, reads are done by Vela.Analytics directly.
/// </summary>
public interface IOrderRejectionsRepository
{
    /// <summary>Writes a new order rejection row.</summary>
    Task SaveAsync(OrderRejection rejection, CancellationToken ct = default);
}

/// <inheritdoc/>
public class OrderRejectionsRepository : IOrderRejectionsRepository
{
    private readonly VelaDbContext _db;
    private readonly ILogger<OrderRejectionsRepository> _logger;

    public OrderRejectionsRepository(VelaDbContext db, ILogger<OrderRejectionsRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(OrderRejection rejection, CancellationToken ct = default)
    {
        try
        {
            _db.OrderRejections.Add(rejection);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to save order rejection for {Symbol}", rejection.Symbol);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }
}
