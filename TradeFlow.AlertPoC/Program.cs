using TradeFlow.AlertPoC.Models;
using TradeFlow.AlertPoC.Services;

// Token is injected via environment variable — never hardcoded or logged.
// In the Worker Service this will be read through IOptions with validation
// at startup so the app fails fast with a clear message rather than at
// the first API call.
var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("[ERROR] XTRADES_TOKEN environment variable is not set.");
    Console.ResetColor();
    return 1;
}

Console.WriteLine($"[INFO] Token loaded ({token.Length} chars)");

var client = new AlertApiClient(token);
List<Alert> alerts;

try
{
    alerts = await client.GetAlertsAsync();
}
catch (AlertApiException ex)
{
    // AlertApiException is our domain boundary — we don't leak HttpClient
    // or JSON details to the top level, just a clean failure message
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    Console.ResetColor();
    return 1;
}

Console.WriteLine($"\n[INFO] Alerts received: {alerts.Count}");
Console.WriteLine(new string('─', 60));

// Cap at 5 for POC readability — the full pipeline will persist all records
foreach (var alert in alerts.Take(5))
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  ID        : {alert.Id}");
    Console.ResetColor();
    Console.WriteLine($"  Trader    : {alert.UserName}");
    Console.WriteLine($"  Symbol    : {alert.Symbol}");
    Console.WriteLine($"  Side      : {alert.Side}");
    Console.WriteLine($"  Direction : {alert.Direction}");
    Console.WriteLine($"  Strike    : {alert.Strike}");
    Console.WriteLine($"  Expiry    : {alert.Expiration}");
    Console.WriteLine($"  Price     : {alert.ActualPriceAtTimeOfAlert}");
    Console.WriteLine($"  Status    : {alert.Status}");
    Console.WriteLine(new string('─', 60));
}

return 0;