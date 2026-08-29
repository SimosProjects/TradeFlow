using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vela.Analytics;
using Vela.Worker.Data;

namespace Vela.Tests.Unit;

public class AnalyticsEngineTests
{
    // -- Builders --

    private static TradeMetric BuildTrade(
        string id,
        DateTimeOffset alertReceivedAt,
        DateTimeOffset? closedAt = null,
        decimal? pnl = null,
        string traderName = "SPYGLASS",
        string symbol = "AAPL") => new()
    {
        Id              = id,
        TraderName      = traderName,
        Symbol          = symbol,
        TradeType       = "Stock",
        AlertReceivedAt = alertReceivedAt,
        OrderSubmittedAt = alertReceivedAt,
        OrderFilledAt   = alertReceivedAt,
        ClosedAt        = closedAt,
        PnL             = pnl,
        Outcome         = closedAt.HasValue ? "XtradesExit" : null,
    };

    private static VelaDbContext BuildDb(params TradeMetric[] trades)
    {
        var db = new VelaDbContext(new DbContextOptionsBuilder<VelaDbContext>()
            .UseInMemoryDatabase($"analytics_{Guid.NewGuid():N}")
            .Options);
        db.TradeMetrics.AddRange(trades);
        db.SaveChanges();
        return db;
    }

    // -- RunAsync: report window is scoped by ClosedAt for realized-P&L sections, not
    // AlertReceivedAt, so a trade spanning multiple report windows is never silently dropped --

    [Fact]
    public async Task RunAsync_TradeOpenedInOneWindowAndClosedTwoWindowsLater_CountsTowardCloseWindowOnly()
    {
        var weekN       = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var weekNPlus2  = weekN.AddDays(14);

        var trade = BuildTrade(
            id: "spanning-trade",
            alertReceivedAt: weekN.AddDays(1),
            closedAt: weekNPlus2.AddDays(2),
            pnl: 250m);

        using var db = BuildDb(trade);
        var engine = new AnalyticsEngine(db, NullLogger<AnalyticsEngine>.Instance);

        var weekNReport = await engine.RunAsync(new AnalyticsOptions
        {
            Report = ReportType.Custom,
            From   = weekN,
            To     = weekN.AddDays(7),
        });
        var weekNPlus2Report = await engine.RunAsync(new AnalyticsOptions
        {
            Report = ReportType.Custom,
            From   = weekNPlus2,
            To     = weekNPlus2.AddDays(7),
        });

        // Week N saw the entry, but the close falls outside its window
        weekNReport.TotalTrades.Should().Be(1);
        weekNReport.ClosedTrades.Should().Be(0);
        weekNReport.TotalPnL.Should().Be(0);
        weekNReport.TraderBreakdown.Should().BeEmpty();
        weekNReport.SymbolBreakdown.Should().BeEmpty();

        // Week N+2 never saw the entry (AlertReceivedAt is two windows earlier), but the close
        // and its P&L must still land here, not vanish between reports
        weekNPlus2Report.TotalTrades.Should().Be(0);
        weekNPlus2Report.ClosedTrades.Should().Be(1);
        weekNPlus2Report.TotalPnL.Should().Be(250m);
        weekNPlus2Report.TraderBreakdown.Should()
            .ContainSingle(t => t.TraderName == "SPYGLASS" && t.TotalPnL == 250m);
        weekNPlus2Report.SymbolBreakdown.Should()
            .ContainSingle(s => s.Symbol == "AAPL" && s.TotalPnL == 250m);
    }

    [Fact]
    public async Task RunAsync_TradeOpenedWellBeforeWindow_ClosedInsideWindow_IsNotDroppedFromReport()
    {
        var windowStart = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

        var trade = BuildTrade(
            id: "opened-long-before",
            alertReceivedAt: windowStart.AddDays(-18),
            closedAt: windowStart.AddDays(3),
            pnl: 648.52m,
            symbol: "MRK");

        using var db = BuildDb(trade);
        var engine = new AnalyticsEngine(db, NullLogger<AnalyticsEngine>.Instance);

        var report = await engine.RunAsync(new AnalyticsOptions
        {
            Report = ReportType.Custom,
            From   = windowStart,
            To     = windowStart.AddDays(7),
        });

        report.ClosedTrades.Should().Be(1);
        report.Wins.Should().Be(1);
        report.TotalPnL.Should().Be(648.52m);
        report.TraderBreakdown.Should().ContainSingle(t => t.TraderName == "SPYGLASS" && t.TotalPnL == 648.52m);
        report.SymbolBreakdown.Should().ContainSingle(s => s.Symbol == "MRK" && s.TotalPnL == 648.52m);
    }

    // -- ClassifyRejection --

    [Theory]
    [InlineData("PRICE_PROTECTION:5.90", EventCategory.Detected, "Entry Declined — Safety Check")]
    [InlineData("NBBO_REJECTION", EventCategory.Detected, "Entry Declined — Safety Check")]
    [InlineData("Cancelled — no position confirmed after fill window", EventCategory.Detected, "Entry Declined — Safety Check")]
    [InlineData("Gateway unreachable", EventCategory.FlaggedForReview, "Entry Failed — Requires Review")]
    [InlineData(null, EventCategory.FlaggedForReview, "Entry Failed — Requires Review")]
    [InlineData("", EventCategory.FlaggedForReview, "Entry Failed — Requires Review")]
    [InlineData("   ", EventCategory.FlaggedForReview, "Entry Failed — Requires Review")]
    public void ClassifyRejection_ReturnsExpectedCategoryAndLabel(
        string? reason, EventCategory expectedCategory, string expectedLabel)
    {
        var (category, label) = AnalyticsEngine.ClassifyRejection(reason);

        category.Should().Be(expectedCategory);
        label.Should().Be(expectedLabel);
    }

    [Fact]
    public void ClassifyRejection_NullReason_DoesNotThrow()
    {
        var act = () => AnalyticsEngine.ClassifyRejection(null);

        act.Should().NotThrow();
    }

    // -- ClassifyReconciliationEvent --

    [Theory]
    [InlineData("ShortCovered", EventCategory.AutoCorrected, "Short Position Automatically Covered")]
    [InlineData("ShortCoverFailed", EventCategory.FlaggedForReview, "Short Cover Attempt Failed")]
    [InlineData("GhostPositionRemoved", EventCategory.AutoCorrected, "Stale Position Automatically Removed")]
    [InlineData("ShortOrZeroPositionRemoved", EventCategory.AutoCorrected, "Closed Position Automatically Removed")]
    [InlineData("QuantityMismatchCorrected", EventCategory.AutoCorrected, "Position Quantity Automatically Reconciled")]
    [InlineData("RepairSucceeded", EventCategory.OperatorConfirmed, "Protective Order Repaired (Operator Confirmed)")]
    [InlineData("ManualPositionDetected", EventCategory.Detected, "Manually-Placed Trade Recognized & Tracked")]
    [InlineData("UnknownOrderDetected", EventCategory.FlaggedForReview, "Unrecognized Order Flagged for Review")]
    [InlineData("PositionMissWarning", EventCategory.FlaggedForReview, "Position Miss Flagged")]
    [InlineData("ManualPositionClosed", EventCategory.AutoCorrected, "Manual Position Tracking Automatically Closed")]
    [InlineData("OrphanedPosition", EventCategory.FlaggedForReview, "Untracked Unprotected Position Flagged")]
    [InlineData("AmbiguousProtection", EventCategory.FlaggedForReview, "Ambiguous Protective Orders Flagged")]
    [InlineData("OrderNotConfirmedLive", EventCategory.FlaggedForReview, "Stop Placement Unconfirmed")]
    [InlineData("RepairFailed", EventCategory.FlaggedForReview, "Protective Order Repair Failed")]
    [InlineData("StopPlacementRejected", EventCategory.FlaggedForReview, "Stop Order Placement Rejected")]
    [InlineData("StillUnprotected", EventCategory.FlaggedForReview, "Position Still Unprotected (Final Check)")]
    [InlineData("DuplicateStopDetected", EventCategory.FlaggedForReview, "Duplicate Stop Orders Flagged")]
    public void ClassifyReconciliationEvent_MappedType_ReturnsConfirmedCategoryAndLabel(
        string eventType, EventCategory expectedCategory, string expectedLabel)
    {
        var (category, label) = AnalyticsEngine.ClassifyReconciliationEvent(eventType);

        category.Should().Be(expectedCategory);
        label.Should().Be(expectedLabel);
    }

    [Fact]
    public void ClassifyReconciliationEvent_UnmappedType_FallsBackToFlaggedForReview()
    {
        var (category, label) = AnalyticsEngine.ClassifyReconciliationEvent("SomeFutureEventType");

        category.Should().Be(EventCategory.FlaggedForReview);
        label.Should().Be("SomeFutureEventType");
    }

    // -- CalculatePartialFillStats --

    [Fact]
    public void CalculatePartialFillStats_LegacyRowWithZeroRequestedQuantity_ExcludedFromAllMetrics()
    {
        var trades = new List<TradeMetric>
        {
            new() { Quantity = 3, RequestedQuantity = 0 },
        };

        var (count, ratePct, avgRatioPct) = AnalyticsEngine.CalculatePartialFillStats(trades);

        count.Should().Be(0);
        ratePct.Should().Be(0);
        avgRatioPct.Should().Be(0);
    }

    [Fact]
    public void CalculatePartialFillStats_GenuinePartialFill_ComputesCorrectRatio()
    {
        var trades = new List<TradeMetric>
        {
            new() { Quantity = 6, RequestedQuantity = 10 },
        };

        var (count, ratePct, avgRatioPct) = AnalyticsEngine.CalculatePartialFillStats(trades);

        count.Should().Be(1);
        ratePct.Should().Be(100m);
        avgRatioPct.Should().Be(60.0m);
    }

    [Fact]
    public void CalculatePartialFillStats_FullFillAtRequestedSize_NotCountedAsPartial()
    {
        var trades = new List<TradeMetric>
        {
            new() { Quantity = 10, RequestedQuantity = 10 },
        };

        var (count, ratePct, avgRatioPct) = AnalyticsEngine.CalculatePartialFillStats(trades);

        count.Should().Be(0);
        ratePct.Should().Be(0);
        avgRatioPct.Should().Be(100m);
    }

    [Fact]
    public void CalculatePartialFillStats_MixedLegacyPartialAndFullFills_LegacyRowExcludedFromDenominator()
    {
        var trades = new List<TradeMetric>
        {
            new() { Quantity = 3, RequestedQuantity = 0 },  // legacy, must not count in denominator
            new() { Quantity = 7, RequestedQuantity = 10 }, // partial
            new() { Quantity = 5, RequestedQuantity = 5 },  // full
        };

        var (count, ratePct, avgRatioPct) = AnalyticsEngine.CalculatePartialFillStats(trades);

        count.Should().Be(1);
        ratePct.Should().Be(50.0m);
        avgRatioPct.Should().Be(85.0m);
    }
}
