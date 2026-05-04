using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── 1. Read token from environment ──────────────────────────────────────────
var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN");

if (string.IsNullOrWhiteSpace(token))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("[ERROR] XTRADES_TOKEN environment variable is not set.");
    Console.ResetColor();
    return 1;
}

Console.WriteLine("[INFO] Token loaded. Length: " + token.Length + " chars (not printed for security)");

// ── 2. Build HTTP client ─────────────────────────────────────────────────────
using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", token);
httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
httpClient.Timeout = TimeSpan.FromSeconds(15);

// ── 3. Build request URL ─────────────────────────────────────────────────────
const string url =
    "https://app.xtrades.net/api/v2/alerts" +
    "?DateSpec=ThreeDays" +
    "&Page=1" +
    "&PageSize=10" +
    "&OrderBy=alertOpenClosedDateEpoch%20desc" +
    "&AlertType=all";

Console.WriteLine($"[INFO] Requesting: {url}\n");

// ── 4. Call the API ──────────────────────────────────────────────────────────
HttpResponseMessage response;

try
{
    response = await httpClient.GetAsync(url);
}
catch (HttpRequestException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[ERROR] Network error: {ex.Message}");
    Console.ResetColor();
    return 1;
}
catch (TaskCanceledException)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("[ERROR] Request timed out.");
    Console.ResetColor();
    return 1;
}

// ── 5. Print HTTP status ─────────────────────────────────────────────────────
Console.ForegroundColor = response.IsSuccessStatusCode ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine($"[HTTP] Status: {(int)response.StatusCode} {response.ReasonPhrase}");
Console.ResetColor();

if (!response.IsSuccessStatusCode)
{
    var errorBody = await response.Content.ReadAsStringAsync();
    Console.Error.WriteLine($"[ERROR] Response body:\n{errorBody}");
    return 1;
}

// ── 6. Deserialize and print alerts ─────────────────────────────────────────
var json = await response.Content.ReadAsStringAsync();

AlertsResponse? result;
try
{
    result = JsonSerializer.Deserialize<AlertsResponse>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });
}
catch (JsonException ex)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Error.WriteLine($"[WARN] Could not deserialize response: {ex.Message}");
    Console.WriteLine("\n[RAW JSON PREVIEW]\n" + json[..Math.Min(json.Length, 800)]);
    Console.ResetColor();
    return 1;
}

var alerts = result?.Alerts ?? result?.Data ?? result?.Items ?? [];

Console.WriteLine($"\n[INFO] Alerts received: {alerts.Count}\n");
Console.WriteLine(new string('─', 60));

foreach (var alert in alerts.Take(5))
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  ID        : {alert.Id}");
    Console.ResetColor();
    Console.WriteLine($"  Trader    : {alert.UserName}");
    Console.WriteLine($"  Symbol    : {alert.Symbol}");
    Console.WriteLine($"  Side      : {alert.Side}");
    Console.WriteLine($"  Type      : {alert.Type}");
    Console.WriteLine($"  Direction : {alert.Direction}");
    Console.WriteLine($"  Strike    : {alert.Strike}");
    Console.WriteLine($"  Expiry    : {alert.Expiration}");
    Console.WriteLine($"  Contract  : {alert.OptionsContractSymbol}");
    Console.WriteLine($"  Price     : {alert.ActualPriceAtTimeOfAlert}");
    Console.WriteLine($"  Time      : {alert.TimeOfEntryAlert}");
    Console.WriteLine($"  Status    : {alert.Status}");
    Console.WriteLine($"  Message   : {alert.OriginalMessage}");
    Console.WriteLine(new string('─', 60));
}

if (alerts.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[WARN] No alerts in response. Raw JSON preview:");
    Console.ResetColor();
    Console.WriteLine(json[..Math.Min(json.Length, 800)]);
}

return 0;

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Flexible wrapper — tries common root-level list field names.
/// Adjust the property that actually matches the Xtrades response shape.
/// </summary>
public class AlertsResponse
{
    [JsonPropertyName("alerts")]
    public List<Alert>? Alerts { get; set; }

    [JsonPropertyName("data")]
    public List<Alert>? Data { get; set; }

    [JsonPropertyName("items")]
    public List<Alert>? Items { get; set; }
}

public class Alert
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("side")]
    public string? Side { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("strike")]
    public decimal? Strike { get; set; }

    [JsonPropertyName("expiration")]
    public string? Expiration { get; set; }

    [JsonPropertyName("optionsContractSymbol")]
    public string? OptionsContractSymbol { get; set; }

    [JsonPropertyName("actualPriceAtTimeOfAlert")]
    public decimal? ActualPriceAtTimeOfAlert { get; set; }

    [JsonPropertyName("timeOfEntryAlert")]
    public string? TimeOfEntryAlert { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("originalMessage")]
    public string? OriginalMessage { get; set; }
}