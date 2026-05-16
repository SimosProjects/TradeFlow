using System.Text.Json;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Thin HTTP client for the Xtrades alerts endpoint. Fetches and deserializes alerts only.
/// The HttpClient is injected by IHttpClientFactory with base address, auth headers,
/// and resilience policies configured in Program.cs.
/// </summary>
public class AlertApiClient : IAlertApiClient
{
    private readonly HttpClient _httpClient;

    public AlertApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Fetches the most recent alerts from the Xtrades API.
    /// Throws <see cref="AlertApiException"/> for any network, HTTP, or
    /// deserialization failure so callers don't need to know the HTTP details.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="pageSize">Number of alerts to fetch. Defaults to 10 for normal polling, use 100 for recovery.</param>
    /// <returns>List of alerts, or an empty list if none are available.</returns>
    public async Task<List<Alert>> GetAlertsAsync(
        CancellationToken cancellationToken = default,
        int pageSize = 10)
    {
        var path = "/api/v2/alerts" +
            "?DateSpec=Today" +
            "&Page=1" +
            $"&PageSize={pageSize}" +
            "&OrderBy=TimeOfEntryAlertEpoch%20desc" +
            "&AlertType=all";

        HttpResponseMessage response;

        try
        {
            response = await _httpClient.GetAsync(path, cancellationToken);
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

        // 204 No Content means no alerts are available (expected outside market hours)
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return [];

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        AlertsResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<AlertsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new AlertApiException(
                $"Failed to deserialize response: {ex.Message}", ex);
        }

        // Coalesce across the three candidate field names, whichever is populated wins
        return result?.Alerts ?? result?.Data ?? result?.Items ?? [];
    }
}