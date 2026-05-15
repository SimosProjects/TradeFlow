using System.Text.Json;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Thin HTTP client for the Xtrades alerts endpoint.
/// Fetch and deserialize alerts only.
///
/// HttpClient is injected by IHttpClientFactory, base address, auth headers,
/// and resilience policies (retry, circuit breaker, timeout) are configured
/// in Program.cs via AddHttpClient and AddStandardResilienceHandler.
/// </summary>
public class AlertApiClient : IAlertApiClient
{
    private readonly HttpClient _httpClient;

    // Path only — base address is set on the injected HttpClient by the factory
    private const string AlertsPath =
        "/api/v2/alerts" +
        "?DateSpec=Today" +
        "&Page=1" +
        "&PageSize=10" +
        "&OrderBy=TimeOfEntryAlertEpoch%20desc" +
        "&AlertType=all";

    public AlertApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Fetches the most recent alerts from the Xtrades API.
    /// Throws <see cref="AlertApiException"/> for any network, HTTP, or
    /// deserialization failure so callers don't need to know the HTTP details.
    /// Pass a CancellationToken to support cooperative cancellation,
    /// the token is propagated to every async operation in the chain.
    /// </summary>
    public async Task<List<Alert>> GetAlertsAsync(
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.GetAsync(AlertsPath, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Covers DNS failure, refused connection, transport errors
            throw new AlertApiException($"Network error: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
            when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient fires TaskCanceledException on timeout, not TimeoutException
            throw new AlertApiException("Request timed out.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AlertApiException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        // 204 No Content, no alerts available
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return new List<Alert>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        AlertsResponse? result;
        try
        {
            // Case-insensitive matching handles any casing drift in the API response
            result = JsonSerializer.Deserialize<AlertsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new AlertApiException(
                $"Failed to deserialize response: {ex.Message}", ex);
        }

        // Coalesce across the three candidate field names, whichever is populated wins.
        // Empty list rather than null keeps callers free of null checks.
        return result?.Alerts ?? result?.Data ?? result?.Items ?? [];
    }
}