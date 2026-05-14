using System.Net.Http.Json;
using System.Text.Json;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Sends trading alert notifications to a Discord channel via webhook.
/// Called when an alert is approved by the risk engine, both from
/// the REST polling service and the SignalR live feed.
/// </summary>
public class DiscordNotificationService
{
    private readonly HttpClient                        _httpClient;
    private readonly ILogger<DiscordNotificationService> _logger;
    private readonly string?                           _webhookUrl;

    public DiscordNotificationService(
        ILogger<DiscordNotificationService> logger)
    {
        _logger     = logger;
        _webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        _httpClient = new HttpClient();

        if (string.IsNullOrWhiteSpace(_webhookUrl))
            _logger.LogWarning(
                "DISCORD_WEBHOOK_URL not set — notifications disabled.");
    }

    /// <summary>
    /// Posts an approved alert to Discord as an embedded message.
    /// Silently skips if webhook URL is not configured.
    /// </summary>
    public async Task NotifyApprovedAlertAsync(
        Alert alert,
        AlertClassification classification,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl))
            return;

        try
        {
            var embed = BuildEmbed(alert, classification);
            var payload = new { embeds = new[] { embed } };

            var response = await _httpClient.PostAsJsonAsync(
                _webhookUrl, payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content
                    .ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Discord webhook returned {Status}: {Body}",
                    (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Never let notification failure affect the main pipeline
            _logger.LogWarning(ex,
                "Failed to send Discord notification — continuing.");
        }
    }

    private static object BuildEmbed(
        Alert alert,
        AlertClassification classification)
    {
        // Color: green for calls, red for puts, blue for stock
        var color = classification.Category switch
        {
            AlertCategory.CallOptionEntry => 0x2ECC71,  // green
            AlertCategory.PutOptionEntry  => 0xE74C3C,  // red
            AlertCategory.StockEntry      => 0x3498DB,  // blue
            _                             => 0x95A5A6   // gray
        };

        var title = classification.Category switch
        {
            AlertCategory.CallOptionEntry => $"📈 CALL — **{alert.Symbol}**",
            AlertCategory.PutOptionEntry  => $"📉 PUT — **{alert.Symbol}**",
            AlertCategory.StockEntry      => $"🏦 STOCK — **{alert.Symbol}**",
            _                             => $"⚡ ALERT — **{alert.Symbol}**"
        };

        var description = alert.ContractDescription
            ?? $"{alert.Symbol} {alert.Direction?.ToUpper()} {alert.Side?.ToUpper()}";

        var fields = new List<object>
        {
            Field("Symbol",  alert.Symbol ?? "—",         true),
            Field("Trader",  alert.UserName ?? "—",        true),
            Field("xScore",  alert.XScore?.ToString("F0") ?? "—", true),
        };

        if (alert.PricePaid.HasValue)
            fields.Add(Field("Entry Price", $"${alert.PricePaid:F2}", true));

        if (alert.Strike.HasValue)
            fields.Add(Field("Strike", $"${alert.Strike:F0}", true));

        if (alert.Expiration is not null &&
            DateTimeOffset.TryParse(alert.Expiration, out var exp))
            fields.Add(Field("Expiration", exp.ToString("MMM dd yyyy"), true));

        if (!string.IsNullOrWhiteSpace(alert.Risk))
            fields.Add(Field("Risk", alert.Risk, true));

        if (!string.IsNullOrWhiteSpace(alert.OriginalMessage))
            fields.Add(Field("Message", alert.OriginalMessage, false));

        return new
        {
            title,
            description,
            color,
            fields,
            footer  = new { text = "TradeFlow Alert System" },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };
    }

    private static object Field(string name, string value, bool inline) =>
        new { name, value, inline };
}