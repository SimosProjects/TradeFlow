using Vela.Analytics;
using Vela.Worker.Data;

namespace Vela.Tests.Unit;

public class AnalyticsEngineTests
{
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
