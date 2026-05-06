namespace TradeFlow.AlertPoC.Services;

/// <summary>
/// Defines the contract for fetching alerts from an external source (e.g. Xtrades API).
/// Abstracting behind an interface allows for real HTTP client to be swapped
/// for a stub implementation in unit tests, and for the API client to be replaced.
/// </summary>
public interface IAlertApiClient
{
    Task<List<Alert>> GetAlertsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines the contract for normalizing raw alerts from the API into a consistent format for downstream processing.
/// </summary>
public interface IAlertNormalizer
{
    Alert Normalize(Alert alert);
    bool IsProcessable(Alert alert);
}