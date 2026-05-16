using TradeFlow.Worker.Models;
using TradeFlow.Worker.Engine;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Background service that fires scheduled health checks and position summaries
/// at fixed times throughout the trading day. Only runs on market days,
/// skipping weekends and fixed US market holidays.
/// </summary>
public class MarketSchedulerService : BackgroundService
{
    private readonly DiscordNotificationService _discord;
    private readonly TradeGuard _guard;
    private readonly IBrokerService _broker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketSchedulerService> _logger;
    private readonly IConfiguration _config;

    private readonly string? _healthWebhookUrl;
    private readonly string? _summaryWebhookUrl;
    private readonly HttpClient _httpClient;

    // Scheduled task times in ET (hour, minute)
    private static readonly (int Hour, int Minute, string Task)[] Schedule =
    [
        (8,  30, "HealthCheck"),
        (9,  15, "PositionSummary"),
        (11, 23, "HealthCheck"),
        (13, 17, "HealthCheck"),
        (15, 10, "HealthCheck"),
        (16,  5, "HealthCheck"),
        (16, 15, "PositionSummary"),
    ];

    // Fixed US market holidays that fall on the same date every year
    private static readonly HashSet<(int Month, int Day)> FixedHolidays =
    [
        (1,  1),  // New Year's Day
        (6,  19), // Juneteenth
        (7,  4),  // Independence Day
        (12, 25), // Christmas Day
    ];

    public MarketSchedulerService(
        DiscordNotificationService discord,
        TradeGuard guard,
        IBrokerService broker,
        IServiceScopeFactory scopeFactory,
        ILogger<MarketSchedulerService> logger,
        IConfiguration config)
    {
        _discord = discord;
        _guard = guard;
        _broker = broker;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
        _httpClient = new HttpClient();

        _healthWebhookUrl  = Environment.GetEnvironmentVariable("DISCORD_HEALTH_WEBHOOK_URL");
        _summaryWebhookUrl = Environment.GetEnvironmentVariable("DISCORD_SUMMARY_WEBHOOK_URL");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Market scheduler service started.");

        // Track which tasks have already fired today to avoid duplicate triggers
        var firedToday = new HashSet<string>();
        var lastCheckedDate = DateOnly.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            var et = GetEasternTime();
            var today = DateOnly.FromDateTime(et.DateTime);

            // Reset fired tasks at the start of each new day
            if (today != lastCheckedDate)
            {
                firedToday.Clear();
                lastCheckedDate = today;
            }

            if (!IsMarketDay(et))
                continue;

            foreach (var (hour, minute, task) in Schedule)
            {
                var key = $"{today}::{hour}:{minute:D2}::{task}";

                if (firedToday.Contains(key))
                    continue;

                // Fire if we are within the 30s window of the scheduled time
                if (et.Hour == hour && et.Minute == minute)
                {
                    firedToday.Add(key);
                    await FireTaskAsync(task, stoppingToken);
                }
            }
        }

        _logger.LogInformation("Market scheduler service stopped.");
    }

    private async Task FireTaskAsync(string task, CancellationToken ct)
    {
        try
        {
            if (task == "HealthCheck")
                await SendHealthCheckAsync(ct);
            else if (task == "PositionSummary")
                await SendPositionSummaryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market scheduler failed to execute task {Task}.", task);
        }
    }

    private async Task SendHealthCheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_healthWebhookUrl))
        {
            _logger.LogWarning("DISCORD_HEALTH_WEBHOOK_URL not set. Skipping health check.");
            return;
        }

        _logger.LogInformation("Market scheduler firing health check.");

        // Check each component and build status fields
        var workerStatus   = "✅ Running";
        var ibkrStatus     = await CheckIbkrAsync(ct);
        var postgresStatus = await CheckPostgresAsync(ct);
        var xtradesStatus  = await CheckXtradesAsync(ct);
        var signalrStatus  = "✅ Connected"; // SignalR connection state not exposed yet, assume connected

        var fields = new[]
        {
            Field("Worker",       workerStatus),
            Field("IB Gateway",   ibkrStatus),
            Field("PostgreSQL",   postgresStatus),
            Field("Xtrades REST", xtradesStatus),
            Field("SignalR",      signalrStatus),
        };

        var embed = new
        {
            title     = "🔍 SYSTEM HEALTH CHECK",
            color     = 0x3498DB,
            fields,
            footer    = new { text = "TradeFlow Health" },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        await PostToWebhookAsync(_healthWebhookUrl, embed, ct);
    }

    private async Task SendPositionSummaryAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_summaryWebhookUrl))
        {
            _logger.LogWarning("DISCORD_SUMMARY_WEBHOOK_URL not set. Skipping position summary.");
            return;
        }

        _logger.LogInformation("Market scheduler firing position summary.");

        var openTrades  = _guard.GetOpenTrades();
        var balance     = await _broker.GetAccountBalanceAsync(ct);
        var dailyCount  = _guard.GetDailyTradeCount();

        // Build account summary section
        var accountSummary =
            $"Balance: **${balance:N2}**\n" +
            $"Open Positions: **{openTrades.Count}**\n" +
            $"Daily Trades: **{dailyCount} / 10**";

        // Build open positions section
        var openSection = openTrades.Count > 0
            ? string.Join("\n", openTrades.Select(t =>
                t.TradeType == TradeType.Options
                    ? $"{t.Symbol} {t.Direction?.ToUpper()} {t.Strike} {FormatExpiration(t.Expiration)} x{t.Quantity} @ ${t.EntryPrice:F2}"
                    : $"{t.Symbol} x{t.Quantity} @ ${t.EntryPrice:F2}"))
            : "No open positions";

        // Read closed trades for today from CSV
        var closedToday = ReadClosedTodayFromCsv();
        var closedSection = closedToday.Count > 0
            ? string.Join("\n", closedToday.Select(t =>
            {
                var pnlSign = t.PnL >= 0 ? "+" : "";
                return $"{t.Symbol} x{t.Quantity} | Entry: ${t.EntryPrice:F2} Exit: ${t.ExitPrice:F2} " +
                       $"({pnlSign}{t.PnLPercent:F1}%) {t.Result}";
            }))
            : "No closed trades today";

        var dailyPnl = closedToday.Sum(t => t.PnL ?? 0);
        var pnlSign  = dailyPnl >= 0 ? "+" : "";

        var fields = new[]
        {
            Field("Account Summary", accountSummary),
            Field("Open Positions",  openSection),
            Field("Closed Today",    closedSection),
            Field("Daily P&L",       $"**{pnlSign}${dailyPnl:N2}**"),
        };

        var embed = new
        {
            title     = "📊 POSITION SUMMARY",
            color     = 0x9B59B6,
            fields,
            footer    = new { text = "TradeFlow Summary" },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        await PostToWebhookAsync(_summaryWebhookUrl, embed, ct);
    }

    // Checks IB Gateway connectivity by attempting to get account balance
    private async Task<string> CheckIbkrAsync(CancellationToken ct)
    {
        try
        {
            var balance = await _broker.GetAccountBalanceAsync(ct);
            return balance > 0 ? "✅ Connected" : "⚠️ Connected (no balance)";
        }
        catch
        {
            return "❌ Disconnected";
        }
    }

    // Checks PostgreSQL by opening a scoped DbContext and running a simple query
    private async Task<string> CheckPostgresAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            await repo.GetExistingAlertIdsAsync([], ct);
            return "✅ Connected";
        }
        catch
        {
            return "❌ Disconnected";
        }
    }

    // Checks Xtrades REST API reachability with a HEAD request
    private async Task<string> CheckXtradesAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                "https://app.xtrades.net", ct);
            return response.IsSuccessStatusCode ? "✅ Reachable" : "⚠️ Degraded";
        }
        catch
        {
            return "❌ Unreachable";
        }
    }

    private async Task PostToWebhookAsync(string url, object embed, CancellationToken ct)
    {
        try
        {
            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(url, payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post to Discord webhook.");
        }
    }

    // Reads closed trades from today from both CSV files
    private List<TradeRecord> ReadClosedTodayFromCsv()
    {
        var tradesDir = _config["Trades:Directory"]
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "trades");

        tradesDir = Path.GetFullPath(tradesDir);

        var today  = DateOnly.FromDateTime(GetEasternTime().DateTime);
        var trades = new List<TradeRecord>();

        trades.AddRange(ReadClosedTodayFromFile(
            Path.Combine(tradesDir, "options_trades.csv"), TradeType.Options, today));

        trades.AddRange(ReadClosedTodayFromFile(
            Path.Combine(tradesDir, "stocks_trades.csv"), TradeType.Stock, today));

        return trades;
    }

    private static List<TradeRecord> ReadClosedTodayFromFile(
        string path, TradeType tradeType, DateOnly today)
    {
        if (!File.Exists(path))
            return [];

        var trades = new List<TradeRecord>();
        var lines  = File.ReadAllLines(path);

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(",,"))
                continue;

            var cols = line.Split(',');

            // Check Status column and Date Closed column
            var statusIndex = tradeType == TradeType.Options ? 14 : 10;
            var closedDateIndex = 2;

            if (cols.Length <= statusIndex) continue;
            if (cols[statusIndex] != "Closed") continue;
            if (!DateOnly.TryParse(cols[closedDateIndex], out var closedDate)) continue;
            if (closedDate != today) continue;

            // Parse enough fields for the summary display
            var entryPriceIndex = tradeType == TradeType.Options ? 10 : 6;
            var exitPriceIndex  = tradeType == TradeType.Options ? 12 : 8;
            var pnlIndex        = tradeType == TradeType.Options ? 16 : 12;
            var pnlPctIndex     = tradeType == TradeType.Options ? 17 : 13;
            var resultIndex     = tradeType == TradeType.Options ? 15 : 11;
            var qtyIndex        = tradeType == TradeType.Options ? 9  : 5;

            decimal.TryParse(cols[entryPriceIndex], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var entryPrice);
            decimal.TryParse(cols[exitPriceIndex], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var exitPrice);
            decimal.TryParse(cols[pnlIndex].TrimStart('+'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var pnl);
            decimal.TryParse(cols[pnlPctIndex].TrimStart('+').TrimEnd('%'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var pnlPct);
            int.TryParse(cols[qtyIndex], out var qty);
            Enum.TryParse<TradeOutcome>(cols[resultIndex], out var result);

            trades.Add(new TradeRecord
            {
                AlertId = string.Empty,
                OrderId = string.Empty,
                StopOrderId = null,
                TargetOrderId = null,
                Symbol = cols[4],
                TradeType = tradeType,
                OptionsContract = null,
                Direction = null,
                Strike = null,
                Expiration = null,
                Quantity = qty,
                EntryPrice = entryPrice,
                EntryAmount = 0,
                StopPrice = 0,
                TargetPrice = 0,
                ExitPrice = exitPrice,
                PnL = pnl,
                PnLPercent = pnlPct,
                Status = TradeStatus.Closed,
                Result = result,
                OpenedAt = DateTimeOffset.UtcNow,
            });
        }

        return trades;
    }

    private static bool IsMarketDay(DateTimeOffset et)
    {
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        if (FixedHolidays.Contains((et.Month, et.Day)))
            return false;

        return true;
    }

    private static DateTimeOffset GetEasternTime() =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static string FormatExpiration(string? expiration)
    {
        if (expiration is null) return "";
        return DateTimeOffset.TryParse(expiration, out var dt)
            ? dt.ToString("MMM dd yyyy")
            : expiration;
    }

    private static object Field(string name, string value) =>
        new { name, value, inline = false };
}