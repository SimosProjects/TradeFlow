using System.Net.Http.Headers;
using System.Text.Json;

namespace TradeFlow.AlertPoC.Services;

/// <summary>
/// Thin HTTP client for the Xtrades v2 alerts endpoint.
/// Responsibility: fetch and deserialize alerts only.
/// Risk filtering, deduplication, and execution live in separate services.
/// </summary>
public class AlertApiClient
{
    private readonly HttpClient _httpClient;

    // Hardcoded for the POC. In the Worker Service this will move to
    // IOptions<XtradesOptions> so values are configurable per environment.
    private const string AlertsUrl =
        "https://app.xtrades.net/api/v2/alerts" +
        "?DateSpec=ThreeDays" +
        "&Page=1" +
        "&PageSize=10" +
        "&OrderBy=alertOpenClosedDateEpoch%20desc" +
        "&AlertType=all";

    public AlertApiClient(string token)
    {
        // Defensive check - fail loudly at construction rather than getting
        // a cryptic 401 on the first request with no indication why
        ArgumentException.ThrowIfNullOrWhiteSpace(token, nameof(token));

        _httpClient = new HttpClient();

        // Bearer token auth — Xtrades uses JWT issued by their frontend.
        // Token is read from environment, never from source control.
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

        // Generous timeout for the POC — the Worker Service will use
        // CancellationToken with a tighter per-request deadline instead.
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Fetches the most recent alerts from the Xtrades API.
    /// Throws <see cref="AlertApiException"/> for any network, HTTP, or
    /// deserialization failure so callers don't need to know the HTTP details.
    /// </summary>
    public async Task<List<Alert>> GetAlertsAsync()
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.GetAsync(AlertsUrl);
        }
        catch (HttpRequestException ex)
        {
            // Covers DNS failure, refused connection, transport errors
            throw new AlertApiException($"Network error: {ex.Message}", ex);
        }
        catch (TaskCanceledException)
        {
            // HttpClient fires TaskCanceledException on timeout, not TimeoutException
            throw new AlertApiException("Request timed out.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new AlertApiException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync();

        AlertsResponse? result;
        try
        {
            // Case-insensitive matching handles any casing drift in the API response
            result = JsonSerializer.Deserialize<AlertsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new AlertApiException($"Failed to deserialize response: {ex.Message}", ex);
        }

        // Coalesce across the three candidate field names — whichever is populated wins.
        // Empty list rather than null keeps callers free of null checks.
        return result?.Alerts ?? result?.Data ?? result?.Items ?? [];
    }
}

/// <summary>
/// Represents any failure that occurred while communicating with the Xtrades API.
/// Wrapping HTTP and network exceptions in a domain-specific exception means
/// callers depend on our abstraction, not on HttpClient internals.
/// </summary>
public class AlertApiException : Exception
{
    public AlertApiException(string message) : base(message) { }
    public AlertApiException(string message, Exception inner) : base(message, inner) { }
}