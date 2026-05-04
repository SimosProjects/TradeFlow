using TradeFlow.AlertPoC.Models;

namespace TradeFlow.AlertPoC.Services;

/// <summary>
/// Service responsible for normalizing raw alerts from the API into a consistent format for downstream processing.
/// </summary>
public static class AlertNormalizer
{
    /// <summary>
    /// Normalizes an alert by trimming whitespace and standardizing casing on key string properties.
    /// This helps downstream classification and deduplication logic be more robust to minor variations in the API response.
    /// </summary>
    public static Alert Normalize(Alert alert) => alert with
    {
        // Normalize symbol to uppercase for consistent downstream processing
        Symbol = alert.Symbol?.ToUpperInvariant(),

        // Normalize side to lowercase for consistent classification logic
        Side = alert.Side?.ToLowerInvariant(),

        // Normalize type to lowercase for consistent classification logic
        Type = alert.Type?.ToLowerInvariant(),

        // Normalize direction to lowercase for consistent classification logic
        Direction = alert.Direction?.ToLowerInvariant(),

        // Trim whitespace from the original message to clean up any formatting inconsistencies
        OriginalMessage = alert.OriginalMessage?.Trim()
    };

    /// <summary>
    /// Returns true if the alert has all the required properties to be processed further (e.g. classified and executed).
    /// </summary>
    /// <param name="alert"></param>
    /// <returns></returns>
    public static bool IsProcessable(Alert alert) =>
        alert.Id is not null &&
        alert.Symbol is not null &&
        alert.Side is not null &&
        alert.Type is not null;
}