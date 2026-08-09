namespace Vela.Analytics;

/// <summary>
/// Strongly typed container for all calculated analytics values.
/// Passed to the report generator after all queries are complete.
/// </summary>
public class ReportData
{
    public ReportType ReportType { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }

    // -- Overview --
    public int TotalAlerts { get; init; }
    public int TotalTrades { get; init; }
    public int OpenTrades { get; init; }
    public int ClosedTrades { get; init; }
    public decimal FilterRatePct { get; init; }        
    public decimal OptionsTradesPct { get; init; }
    public decimal StockTradesPct { get; init; }

    // -- Win/Loss --
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int BreakEvens { get; init; }
    public decimal WinRatePct { get; init; }
    public decimal AvgWinPct { get; init; }         
    public decimal AvgLossPct { get; init; }         
    public decimal AvgPnLPerTrade { get; init; }       
    public decimal TotalPnL { get; init; }
    public decimal LargestWin { get; init; }
    public decimal LargestLoss { get; init; }
    public int MaxConsecutiveLosses { get; init; }

    // -- Latency --
    public double AvgLatencyMs { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double MaxLatencyMs { get; init; }

    // -- Slippage --
    public decimal AvgSlippagePct { get; init; }
    public decimal MaxSlippagePct { get; init; }

    // -- Exposure --
    public decimal AvgExposurePct { get; init; }
    public decimal MaxExposurePct { get; init; }

    // -- Outcome breakdown --
    public int TargetHits { get; init; }
    public int StoppedOuts { get; init; }
    public int XtradesExits { get; init; }

    // -- Per trader --
    public List<TraderStats> TraderBreakdown { get; init; } = [];

    // -- Per symbol --
    public List<SymbolStats> SymbolBreakdown { get; init; } = [];

    // -- Daily P&L series for chart --
    public List<DailyPnL> DailyPnLSeries { get; init; } = [];

    // -- All trades for detail table --
    public List<TradeRow> AllTrades { get; init; } = [];

    // -- Execution Quality --
    public int TotalEntryAttempts { get; init; }
    public int OrdersRejectedCount { get; init; }
    public decimal RejectionRatePct { get; init; }
    public int PartialFillCount { get; init; }
    public decimal PartialFillRatePct { get; init; }
    public decimal AvgFillRatioPct { get; init; }
    public List<OrderRejectionRow> RejectionRows { get; init; } = [];

    // -- System Reliability --
    public int TotalHealthChecks { get; init; }
    public decimal IbkrUptimePct { get; init; }
    public decimal PostgresUptimePct { get; init; }
    public decimal XtradesUptimePct { get; init; }
    public List<HealthIncidentRow> HealthIncidents { get; init; } = [];

    // -- Reconciliation Integrity --
    public int TotalReconciliationEvents { get; init; }
    public int AutoCorrectedCount { get; init; }
    public int OperatorConfirmedCount { get; init; }
    public int DetectedCount { get; init; }
    public int FlaggedForReviewCount { get; init; }
    public List<ReconciliationTypeStats> ReconciliationTypeBreakdown { get; init; } = [];
}

/// <summary>
/// Customer-facing classification for a rejection or reconciliation event.
/// AutoCorrected: the system detected and resolved the issue with no operator involvement.
/// OperatorConfirmed: the system detected the issue and proposed a fix; an operator confirmed
/// the specific action (e.g. Vela.Guardian's interactive prompt) and it succeeded.
/// Detected: informational, not a problem (e.g. recognizing a manually-placed trade) or a
/// safety-driven decline where no capital was at risk.
/// FlaggedForReview: the system could not resolve this itself and surfaced it for a human.
/// </summary>
public enum EventCategory
{
    AutoCorrected,
    OperatorConfirmed,
    Detected,
    FlaggedForReview
}

public class OrderRejectionRow
{
    public DateTimeOffset CreatedAt { get; init; }
    public string? Symbol { get; init; }
    public string? TradeType { get; init; }
    public string? TraderName { get; init; }
    public int RequestedQuantity { get; init; }
    public decimal? RequestedPrice { get; init; }
    public string Reason { get; init; } = string.Empty;
    public EventCategory Category { get; init; }
    public string Label { get; init; } = string.Empty;
}

public class HealthIncidentRow
{
    public DateTimeOffset CheckedAt { get; init; }
    public string WorkerStatus { get; init; } = string.Empty;
    public string IbkrStatus { get; init; } = string.Empty;
    public string PostgresStatus { get; init; } = string.Empty;
    public string XtradesStatus { get; init; } = string.Empty;
    public string SignalrStatus { get; init; } = string.Empty;
}

public class ReconciliationTypeStats
{
    public string EventType { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public EventCategory Category { get; init; }
    public int Count { get; init; }
    public decimal PctOfTotal { get; init; }
}

public class TraderStats
{
    public string TraderName { get; init; } = string.Empty;
    public int TotalTrades { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public decimal WinRatePct { get; init; }
    public decimal AvgWinPct { get; init; }
    public decimal AvgLossPct { get; init; }
    public decimal AvgPnLPerTrade { get; init; }
    public decimal TotalPnL { get; init; }
}

public class SymbolStats
{
    public string Symbol { get; init; } = string.Empty;
    public int TotalTrades { get; init; }
    public int Wins { get; init; }
    public decimal WinRatePct { get; init; }
    public decimal AvgPnLPerTrade { get; init; }
    public decimal TotalPnL { get; init; }
}

public class DailyPnL
{
    public DateOnly Date { get; init; }
    public decimal DayPnL { get; init; }
    public decimal CumulativePnL { get; init; }
}

public class TradeRow
{
    public string OrderId { get; init; } = string.Empty;
    public string? TraderName { get; init; }
    public string? Symbol { get; init; }
    public string? TradeType { get; init; }
    public string? Direction { get; init; }
    public bool IsAverage { get; init; }
    public DateTimeOffset AlertReceivedAt { get; init; }
    public int LatencyMs { get; init; }
    public decimal AlertedPrice { get; init; }
    public decimal FillPrice { get; init; }
    public decimal SlippagePct { get; init; }
    public int Quantity { get; init; }
    public decimal EntryAmount { get; init; }
    public decimal StopPrice { get; init; }
    public decimal TargetPrice { get; init; }
    public decimal ExposurePct { get; init; }
    public decimal? ExitPrice { get; init; }
    public decimal? PnL { get; init; }
    public decimal? PnLPct { get; init; }
    public string? Outcome { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>
/// Queries the trade_metrics table and calculates all analytics values
/// for the given date range.
/// </summary>
public class AnalyticsEngine
{
    private readonly VelaDbContext _db;
    private readonly ILogger<AnalyticsEngine> _logger;

    private static readonly TimeZoneInfo Et =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Maps each reconciliation_events EventType to its customer-facing category and label.
    // Reviewed and confirmed 2026-08-09 before this became customer-facing copy. An
    // unrecognized EventType falls back to FlaggedForReview in ClassifyReconciliationEvent
    // rather than being silently treated as resolved.
    private static readonly Dictionary<string, (EventCategory Category, string Label)> ReconciliationEventMap = new()
    {
        ["ShortCovered"]               = (EventCategory.AutoCorrected,    "Short Position Automatically Covered"),
        ["ShortCoverFailed"]           = (EventCategory.FlaggedForReview, "Short Cover Attempt Failed"),
        ["GhostPositionRemoved"]       = (EventCategory.AutoCorrected,    "Stale Position Automatically Removed"),
        ["ShortOrZeroPositionRemoved"] = (EventCategory.AutoCorrected,    "Closed Position Automatically Removed"),
        ["QuantityMismatchCorrected"]  = (EventCategory.AutoCorrected,    "Position Quantity Automatically Reconciled"),
        ["RepairSucceeded"]            = (EventCategory.OperatorConfirmed, "Protective Order Repaired (Operator Confirmed)"),
        ["ManualPositionDetected"]     = (EventCategory.Detected,         "Manually-Placed Trade Recognized & Tracked"),
        ["UnknownOrderDetected"]       = (EventCategory.FlaggedForReview, "Unrecognized Order Flagged for Review"),
        ["PositionMissWarning"]        = (EventCategory.FlaggedForReview, "Position Miss Flagged"),
        ["ManualPositionClosed"]       = (EventCategory.AutoCorrected,    "Manual Position Tracking Automatically Closed"),
        ["OrphanedPosition"]           = (EventCategory.FlaggedForReview, "Untracked Unprotected Position Flagged"),
        ["AmbiguousProtection"]        = (EventCategory.FlaggedForReview, "Ambiguous Protective Orders Flagged"),
        ["OrderNotConfirmedLive"]      = (EventCategory.FlaggedForReview, "Stop Placement Unconfirmed"),
        ["RepairFailed"]               = (EventCategory.FlaggedForReview, "Protective Order Repair Failed"),
        ["StopPlacementRejected"]      = (EventCategory.FlaggedForReview, "Stop Order Placement Rejected"),
        ["StillUnprotected"]           = (EventCategory.FlaggedForReview, "Position Still Unprotected (Final Check)"),
        ["DuplicateStopDetected"]      = (EventCategory.FlaggedForReview, "Duplicate Stop Orders Flagged"),
    };

    // Benign prefixes mean the system declined an entry as a safety measure (price protection,
    // NBBO rejection, or a verified non-fill) — no capital was ever at risk. Anything else,
    // including exception-catch messages like broker connectivity failures, is flagged rather
    // than assumed benign.
    private static readonly string[] BenignRejectionPrefixes =
    [
        "PRICE_PROTECTION:",
        "NBBO_REJECTION",
    ];

    public AnalyticsEngine(VelaDbContext db, ILogger<AnalyticsEngine> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Runs all queries and calculations for the given options, returning
    /// a fully populated ReportData ready for the report generator.
    /// </summary>
    public async Task<ReportData> RunAsync(AnalyticsOptions options)
    {
        _logger.LogInformation(
            "Running analytics: {Report} | {From:yyyy-MM-dd} to {To:yyyy-MM-dd}",
            options.Report, options.From, options.To);

        // Load all trades in the period into memory, volumes are low enough
        // that in-memory calculation is cleaner than complex SQL expressions
        var trades = await _db.TradeMetrics
            .AsNoTracking()
            .Where(m => m.AlertReceivedAt >= options.From
                     && m.AlertReceivedAt <  options.To)
            .OrderBy(m => m.AlertReceivedAt)
            .ToListAsync();

        _logger.LogInformation("Loaded {Count} trade metrics for period.", trades.Count);

        // Total alerts in the same period from the alerts table
        var totalAlerts = await _db.Alerts
            .AsNoTracking()
            .Where(a => a.IngestedAt >= options.From
                     && a.IngestedAt <  options.To)
            .CountAsync();

        var closed = trades.Where(t => t.ClosedAt.HasValue).ToList();
        var open   = trades.Where(t => !t.ClosedAt.HasValue).ToList();

        var wins      = closed.Where(t => t.PnL > 0).ToList();
        var losses    = closed.Where(t => t.PnL < 0).ToList();
        var breakEvens = closed.Where(t => t.PnL == 0).ToList();

        var options_trades = trades.Where(t => t.TradeType == "Options").ToList();
        var stock_trades   = trades.Where(t => t.TradeType == "Stock").ToList();

        // Rejected/cancelled/failed entry attempts in the same period
        var rejections = await _db.OrderRejections
            .AsNoTracking()
            .Where(r => r.CreatedAt >= options.From
                     && r.CreatedAt <  options.To)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        // Reconciliation mismatches detected against IBKR or the CSV trade log
        var reconciliationEvents = await _db.ReconciliationEvents
            .AsNoTracking()
            .Where(e => e.CreatedAt >= options.From
                     && e.CreatedAt <  options.To)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        // Scheduled system health check snapshots
        var healthChecks = await _db.HealthChecks
            .AsNoTracking()
            .Where(h => h.CheckedAt >= options.From
                     && h.CheckedAt <  options.To)
            .OrderBy(h => h.CheckedAt)
            .ToListAsync();

        var (partialFillCount, partialFillRatePct, avgFillRatioPct) = CalculatePartialFillStats(trades);

        var classifiedRejections = rejections
            .Select(r => (Rejection: r, Classification: ClassifyRejection(r.Reason)))
            .ToList();

        var classifiedEvents = reconciliationEvents
            .Select(e => (Event: e, Classification: ClassifyReconciliationEvent(e.EventType)))
            .ToList();

        return new ReportData
        {
            ReportType  = options.Report,
            From        = options.From,
            To          = options.To,
            GeneratedAt = DateTimeOffset.UtcNow,

            // Overview
            TotalAlerts      = totalAlerts,
            TotalTrades      = trades.Count,
            OpenTrades       = open.Count,
            ClosedTrades     = closed.Count,
            FilterRatePct    = totalAlerts > 0
                ? Math.Round((decimal)trades.Count / totalAlerts * 100, 1)
                : 0,
            OptionsTradesPct = trades.Count > 0
                ? Math.Round((decimal)options_trades.Count / trades.Count * 100, 1)
                : 0,
            StockTradesPct   = trades.Count > 0
                ? Math.Round((decimal)stock_trades.Count / trades.Count * 100, 1)
                : 0,

            // Win/Loss
            Wins             = wins.Count,
            Losses           = losses.Count,
            BreakEvens       = breakEvens.Count,
            WinRatePct       = closed.Count > 0
                ? Math.Round((decimal)wins.Count / closed.Count * 100, 1)
                : 0,
            AvgWinPct        = wins.Count > 0
                ? Math.Round(wins.Average(t => t.PnLPct ?? 0), 2)
                : 0,
            AvgLossPct       = losses.Count > 0
                ? Math.Round(losses.Average(t => t.PnLPct ?? 0), 2)
                : 0,
            AvgPnLPerTrade   = closed.Count > 0
                ? Math.Round(closed.Average(t => t.PnL ?? 0), 2)
                : 0,
            TotalPnL         = closed.Sum(t => t.PnL ?? 0),
            LargestWin       = wins.Count > 0
                ? wins.Max(t => t.PnL ?? 0)
                : 0,
            LargestLoss      = losses.Count > 0
                ? losses.Min(t => t.PnL ?? 0)
                : 0,
            MaxConsecutiveLosses = CalculateMaxConsecutiveLosses(closed),

            // Latency
            AvgLatencyMs = trades.Count > 0
                ? Math.Round(trades.Average(t => (double)t.LatencyMs), 0)
                : 0,
            P50LatencyMs = Percentile(trades.Select(t => (double)t.LatencyMs).ToList(), 50),
            P95LatencyMs = Percentile(trades.Select(t => (double)t.LatencyMs).ToList(), 95),
            MaxLatencyMs = trades.Count > 0
                ? trades.Max(t => (double)t.LatencyMs)
                : 0,

            // Slippage
            AvgSlippagePct = trades.Count > 0
                ? Math.Round(trades.Average(t => t.SlippagePct), 3)
                : 0,
            MaxSlippagePct = trades.Count > 0
                ? trades.Max(t => t.SlippagePct)
                : 0,

            // Exposure
            AvgExposurePct = trades.Count > 0
                ? Math.Round(trades.Average(t => t.ExposurePct), 1)
                : 0,
            MaxExposurePct = trades.Count > 0
                ? trades.Max(t => t.ExposurePct)
                : 0,

            // Outcome breakdown
            TargetHits   = closed.Count(t => t.Outcome == "TargetHit"),
            StoppedOuts  = closed.Count(t => t.Outcome == "StoppedOut"),
            XtradesExits = closed.Count(t => t.Outcome == "XtradesExit"),

            // Per trader
            TraderBreakdown = trades
                .GroupBy(t => t.TraderName ?? "Unknown")
                .Select(g =>
                {
                    var traderClosed = g.Where(t => t.ClosedAt.HasValue).ToList();
                    var traderWins   = traderClosed.Where(t => t.PnL > 0).ToList();
                    var traderLosses = traderClosed.Where(t => t.PnL < 0).ToList();
                    return new TraderStats
                    {
                        TraderName     = g.Key,
                        TotalTrades    = g.Count(),
                        Wins           = traderWins.Count,
                        Losses         = traderLosses.Count,
                        WinRatePct     = traderClosed.Count > 0
                            ? Math.Round((decimal)traderWins.Count / traderClosed.Count * 100, 1)
                            : 0,
                        AvgWinPct      = traderWins.Count > 0
                            ? Math.Round(traderWins.Average(t => t.PnLPct ?? 0), 2)
                            : 0,
                        AvgLossPct     = traderLosses.Count > 0
                            ? Math.Round(traderLosses.Average(t => t.PnLPct ?? 0), 2)
                            : 0,
                        AvgPnLPerTrade = traderClosed.Count > 0
                            ? Math.Round(traderClosed.Average(t => t.PnL ?? 0), 2)
                            : 0,
                        TotalPnL       = traderClosed.Sum(t => t.PnL ?? 0),
                    };
                })
                .OrderByDescending(t => t.AvgPnLPerTrade)
                .ToList(),

            // Per symbol
            SymbolBreakdown = trades
                .GroupBy(t => t.Symbol ?? "Unknown")
                .Select(g =>
                {
                    var symClosed = g.Where(t => t.ClosedAt.HasValue).ToList();
                    var symWins   = symClosed.Where(t => t.PnL > 0).ToList();
                    return new SymbolStats
                    {
                        Symbol         = g.Key,
                        TotalTrades    = g.Count(),
                        Wins           = symWins.Count,
                        WinRatePct     = symClosed.Count > 0
                            ? Math.Round((decimal)symWins.Count / symClosed.Count * 100, 1)
                            : 0,
                        AvgPnLPerTrade = symClosed.Count > 0
                            ? Math.Round(symClosed.Average(t => t.PnL ?? 0), 2)
                            : 0,
                        TotalPnL       = symClosed.Sum(t => t.PnL ?? 0),
                    };
                })
                .OrderByDescending(s => s.TotalPnL)
                .ToList(),

            // Daily P&L series, grouped by ET date for accurate market-day alignment
            DailyPnLSeries = BuildDailyPnLSeries(closed),

            // All trades detail table
            AllTrades = trades.Select(t => new TradeRow
            {
                OrderId         = t.Id,
                TraderName      = t.TraderName,
                Symbol          = t.Symbol,
                TradeType       = t.TradeType,
                Direction       = t.Direction,
                IsAverage       = t.IsAverage,
                AlertReceivedAt = t.AlertReceivedAt,
                LatencyMs       = t.LatencyMs,
                AlertedPrice    = t.AlertedPrice,
                FillPrice       = t.FillPrice,
                SlippagePct     = t.SlippagePct,
                Quantity        = t.Quantity,
                EntryAmount     = t.EntryAmount,
                StopPrice       = t.StopPrice,
                TargetPrice     = t.TargetPrice,
                ExposurePct     = t.ExposurePct,
                ExitPrice       = t.ExitPrice,
                PnL             = t.PnL,
                PnLPct          = t.PnLPct,
                Outcome         = t.Outcome,
                ClosedAt        = t.ClosedAt,
            }).ToList(),

            // Execution Quality
            TotalEntryAttempts = trades.Count + rejections.Count,
            OrdersRejectedCount = rejections.Count,
            RejectionRatePct = (trades.Count + rejections.Count) > 0
                ? Math.Round((decimal)rejections.Count / (trades.Count + rejections.Count) * 100, 1)
                : 0,
            PartialFillCount = partialFillCount,
            PartialFillRatePct = partialFillRatePct,
            AvgFillRatioPct = avgFillRatioPct,
            RejectionRows = classifiedRejections.Select(x => new OrderRejectionRow
            {
                CreatedAt         = x.Rejection.CreatedAt,
                Symbol            = x.Rejection.Symbol,
                TradeType         = x.Rejection.TradeType,
                TraderName        = x.Rejection.TraderName,
                RequestedQuantity = x.Rejection.RequestedQuantity,
                RequestedPrice    = x.Rejection.RequestedPrice,
                Reason            = x.Rejection.Reason,
                Category          = x.Classification.Category,
                Label             = x.Classification.Label,
            }).ToList(),

            // System Reliability
            TotalHealthChecks = healthChecks.Count,
            IbkrUptimePct     = UptimePct(healthChecks, h => h.IbkrStatus),
            PostgresUptimePct = UptimePct(healthChecks, h => h.PostgresStatus),
            XtradesUptimePct  = UptimePct(healthChecks, h => h.XtradesStatus),
            HealthIncidents = healthChecks
                .Where(h => !IsHealthy(h.IbkrStatus) || !IsHealthy(h.PostgresStatus) || !IsHealthy(h.XtradesStatus))
                .Select(h => new HealthIncidentRow
                {
                    CheckedAt      = h.CheckedAt,
                    WorkerStatus   = h.WorkerStatus,
                    IbkrStatus     = h.IbkrStatus,
                    PostgresStatus = h.PostgresStatus,
                    XtradesStatus  = h.XtradesStatus,
                    SignalrStatus  = h.SignalrStatus,
                })
                .ToList(),

            // Reconciliation Integrity
            TotalReconciliationEvents = reconciliationEvents.Count,
            AutoCorrectedCount = classifiedEvents.Count(x => x.Classification.Category == EventCategory.AutoCorrected),
            OperatorConfirmedCount = classifiedEvents.Count(x => x.Classification.Category == EventCategory.OperatorConfirmed),
            DetectedCount = classifiedEvents.Count(x => x.Classification.Category == EventCategory.Detected),
            FlaggedForReviewCount = classifiedEvents.Count(x => x.Classification.Category == EventCategory.FlaggedForReview),
            ReconciliationTypeBreakdown = classifiedEvents
                .GroupBy(x => x.Event.EventType)
                .Select(g => new ReconciliationTypeStats
                {
                    EventType  = g.Key,
                    Label      = g.First().Classification.Label,
                    Category   = g.First().Classification.Category,
                    Count      = g.Count(),
                    PctOfTotal = reconciliationEvents.Count > 0
                        ? Math.Round((decimal)g.Count() / reconciliationEvents.Count * 100, 1)
                        : 0,
                })
                .OrderByDescending(s => s.Count)
                .ToList(),
        };
    }

    // Groups closed trades by ET date and builds a cumulative P&L series for the chart
    private static List<DailyPnL> BuildDailyPnLSeries(List<TradeMetric> closed)
    {
        var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var byDay = closed
            .GroupBy(t => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(t.ClosedAt!.Value, et).DateTime))
            .OrderBy(g => g.Key)
            .ToList();

        var cumulative = 0m;
        return byDay.Select(g =>
        {
            var dayPnL = g.Sum(t => t.PnL ?? 0);
            cumulative += dayPnL;
            return new DailyPnL
            {
                Date          = g.Key,
                DayPnL        = Math.Round(dayPnL, 2),
                CumulativePnL = Math.Round(cumulative, 2),
            };
        }).ToList();
    }

    // Walks trades in order and tracks the longest consecutive losing streak
    private static int CalculateMaxConsecutiveLosses(List<TradeMetric> closed)
    {
        int max = 0, current = 0;
        foreach (var trade in closed.OrderBy(t => t.ClosedAt))
        {
            if (trade.PnL < 0)
            {
                current++;
                max = Math.Max(max, current);
            }
            else
            {
                current = 0;
            }
        }
        return max;
    }

    // Calculates a percentile value from a sorted list
    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var index = (percentile / 100.0) * (values.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return values[lower];
        return values[lower] + (index - lower) * (values[upper] - values[lower]);
    }

    // Classifies an order_rejections row from its free-text Reason. See BenignRejectionPrefixes.
    // A null or blank reason falls back to FlaggedForReview rather than throwing — Reason is a
    // non-nullable DB column in practice, but this must never crash a report over bad data.
    internal static (EventCategory Category, string Label) ClassifyRejection(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return (EventCategory.FlaggedForReview, "Entry Failed — Requires Review");

        var isBenign = BenignRejectionPrefixes.Any(p =>
                reason.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || reason.Contains("no position confirmed", StringComparison.OrdinalIgnoreCase);

        return isBenign
            ? (EventCategory.Detected, "Entry Declined — Safety Check")
            : (EventCategory.FlaggedForReview, "Entry Failed — Requires Review");
    }

    // Looks up a reconciliation_events EventType in ReconciliationEventMap. An unrecognized
    // type falls back to FlaggedForReview with its raw name rather than being silently
    // misrepresented as resolved.
    internal static (EventCategory Category, string Label) ClassifyReconciliationEvent(string eventType) =>
        ReconciliationEventMap.TryGetValue(eventType, out var mapped)
            ? mapped
            : (EventCategory.FlaggedForReview, eventType);

    // Computes partial-fill metrics from already-loaded trade_metrics rows. Trades with
    // RequestedQuantity == 0 predate the column (AddRequestedQuantityToTradeMetrics backfilled
    // existing rows to 0) and are excluded rather than misread as full fills.
    internal static (int PartialFillCount, decimal PartialFillRatePct, decimal AvgFillRatioPct)
        CalculatePartialFillStats(List<TradeMetric> trades)
    {
        var eligible = trades.Where(t => t.RequestedQuantity > 0).ToList();
        var partial  = eligible.Where(t => t.Quantity < t.RequestedQuantity).ToList();

        return (
            PartialFillCount: partial.Count,
            PartialFillRatePct: eligible.Count > 0
                ? Math.Round((decimal)partial.Count / eligible.Count * 100, 1)
                : 0,
            AvgFillRatioPct: eligible.Count > 0
                ? Math.Round(eligible.Average(t => (decimal)t.Quantity / t.RequestedQuantity) * 100, 1)
                : 0);
    }

    // A status string is healthy only when it starts with the checkmark the Worker's
    // IBKR/Postgres/Xtrades checks emit on success — degraded (warning) and failure strings
    // both count as not healthy.
    private static bool IsHealthy(string status) => status.StartsWith("✅", StringComparison.Ordinal);

    private static decimal UptimePct(List<HealthCheck> checks, Func<HealthCheck, string> selector) =>
        checks.Count > 0
            ? Math.Round((decimal)checks.Count(c => IsHealthy(selector(c))) / checks.Count * 100, 1)
            : 0;
}