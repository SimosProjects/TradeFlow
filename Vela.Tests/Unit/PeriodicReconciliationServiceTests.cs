using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
using Vela.Worker.Engine;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

/// <summary>
/// Unit tests for PeriodicReconciliationService.CheckManagedPositionsAsync.
/// TradeGuard is a real instance seeded via LoadFromDatabase; the repository
/// and broker are mocked via Moq.
/// </summary>
public class PeriodicReconciliationServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    // -- Helpers --

    private (
        PeriodicReconciliationService Svc,
        TradeGuard Guard,
        Mock<IOpenPositionRepository> Repo,
        Mock<IReconciliationEventsRepository> ReconciliationEvents)
        BuildService()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetAccountBalanceAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(100_000m);
        broker.Setup(b => b.GetOpenPositionsValueAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(0m);

        var guard = new TradeGuard(
            broker.Object,
            Options.Create(new RiskEngineOptions()),
            NullLogger<TradeGuard>.Instance);

        var discord = new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance);

        var repo = new Mock<IOpenPositionRepository>();
        repo.Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // CheckManagedPositionsAsync now also fetches manual positions from the DB — default
        // to none so existing managed-only tests don't need to know about this.
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // No trade_metrics row by default — GhostPositionCloseOutService no-ops, matching
        // the pre-close-out behaviour these tests were written against.
        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((TradeMetric?)null);

        var tempDir = Path.Combine(Path.GetTempPath(), $"vela_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);
        var csvConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Trades:Directory"] = tempDir })
            .Build();
        var csv = new CsvTradeLogger(csvConfig, NullLogger<CsvTradeLogger>.Instance);

        var reconciliationEvents = new Mock<IReconciliationEventsRepository>();
        reconciliationEvents.Setup(r => r.SaveAsync(
                It.IsAny<ReconciliationEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddScoped<IOpenPositionRepository>(_ => repo.Object);
        services.AddScoped<ITradeMetricsRepository>(_ => metrics.Object);
        services.AddScoped<IReconciliationEventsRepository>(_ => reconciliationEvents.Object);
        services.AddScoped(_ => new GhostPositionCloseOutService(
            broker.Object, metrics.Object, csv, NullLogger<GhostPositionCloseOutService>.Instance));
        var scopeFactory = services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var svc = new PeriodicReconciliationService(
            broker.Object,
            guard,
            discord,
            scopeFactory,
            NullLogger<PeriodicReconciliationService>.Instance);

        return (svc, guard, repo, reconciliationEvents);
    }

    private static OpenPosition StockDbPosition(string symbol, string orderId, int qty = 5) =>
        new()
        {
            OrderId     = orderId,
            UserName    = "Theo",
            Symbol      = symbol,
            TradeType   = "Stock",
            Quantity    = qty,
            EntryPrice  = 10m,
            EntryAmount = qty * 10m,
            StopPrice   = 8m,
            TargetPrice = 20m,
            OpenedAt    = DateTimeOffset.UtcNow,
            IsManual    = false,
        };

    private static OpenPosition ManualStockDbPosition(string symbol, string orderId, int qty = 5) =>
        new()
        {
            OrderId     = orderId,
            AlertId     = "MANUAL",
            UserName    = "MANUAL",
            Symbol      = symbol,
            TradeType   = "Stock",
            Quantity    = qty,
            EntryPrice  = 10m,
            EntryAmount = qty * 10m,
            StopPrice   = 0m,
            TargetPrice = 0m,
            OpenedAt    = DateTimeOffset.UtcNow,
            IsManual    = true,
        };

    private static IbkrPosition StockPos(string symbol, int qty) =>
        new(symbol, "STK", null, qty, 10m);

    // -- CheckManagedPositionsAsync --

    [Fact]
    public async Task CheckManagedPositions_WhenIbkrMatchHasZeroQty_TreatedAsMissLikeNoMatch()
    {
        var (svc, guard, repo, reconciliationEvents) = BuildService();
        guard.LoadFromDatabase([StockDbPosition("TSLA", "9905")]);

        // IBKR still returns a row for TSLA, but qty is 0 — position is actually closed.
        // Two consecutive misses matches AutoCleanupAfterMisses, same as a fully-missing row.
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 0)], CancellationToken.None);
        reconciliationEvents.Verify(r => r.SaveAsync(
            It.Is<ReconciliationEvent>(e =>
                e.EventType == "PositionMissWarning" &&
                e.Symbol    == "TSLA" &&
                e.OrderId   == "9905"),
            It.IsAny<CancellationToken>()), Times.Once);

        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 0)], CancellationToken.None);

        repo.Verify(r => r.DeleteAsync("9905", It.IsAny<CancellationToken>()), Times.Once);
        guard.GetOpenTrades().Should().BeEmpty();
        reconciliationEvents.Verify(r => r.SaveAsync(
            It.Is<ReconciliationEvent>(e =>
                e.EventType == "GhostPositionRemoved" &&
                e.Symbol    == "TSLA" &&
                e.OrderId   == "9905"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckManagedPositions_WhenIbkrMatchHasPositiveQty_ClearsMissStreak()
    {
        var (svc, guard, repo, _) = BuildService();
        guard.LoadFromDatabase([StockDbPosition("TSLA", "9905")]);

        // IBKR confirms the position is still open on every cycle — must never be removed.
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 5)], CancellationToken.None);
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 5)], CancellationToken.None);
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 5)], CancellationToken.None);

        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        guard.GetOpenTrades().Should().ContainSingle(t => t.OrderId == "9905");
    }

    // -- Manual (IsManual) positions get the same liveness coverage (2026-07-17/18 MANUAL-SPX incident) --

    [Fact]
    public async Task CheckManagedPositions_ManualPosition_WhenIbkrMatchHasZeroQty_TreatedAsMissLikeNoMatch()
    {
        var (svc, guard, repo, _) = BuildService();
        var manual = ManualStockDbPosition("SPX", "MANUAL-SPX-1784295727968");
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([manual]);

        // IBKR still returns a row for SPX, but qty is 0 — the manual position is actually
        // closed. Must go through the identical two-consecutive-miss cleanup as a managed row,
        // not be silently skipped because it's manual.
        await svc.CheckManagedPositionsAsync([StockPos("SPX", 0)], CancellationToken.None);
        repo.Verify(r => r.DeleteAsync(
            "MANUAL-SPX-1784295727968", It.IsAny<CancellationToken>()), Times.Never,
            "a single miss must only warn, not remove");

        await svc.CheckManagedPositionsAsync([StockPos("SPX", 0)], CancellationToken.None);
        repo.Verify(r => r.DeleteAsync(
            "MANUAL-SPX-1784295727968", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckManagedPositions_ManualPosition_WhenIbkrMatchHasPositiveQty_IsNotTouched()
    {
        var (svc, _, repo, _) = BuildService();
        var manual = ManualStockDbPosition("SPX", "MANUAL-SPX-1784295727968");
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([manual]);

        // IBKR confirms the manual position is still genuinely open on every cycle — must
        // never be removed, same guarantee a managed position already gets.
        await svc.CheckManagedPositionsAsync([StockPos("SPX", 1)], CancellationToken.None);
        await svc.CheckManagedPositionsAsync([StockPos("SPX", 1)], CancellationToken.None);
        await svc.CheckManagedPositionsAsync([StockPos("SPX", 1)], CancellationToken.None);

        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- DetectNewManualPositionsAsync / CleanClosedManualPositionsAsync --

    [Fact]
    public async Task DetectNewManualPositions_WhenUntrackedLongExists_LogsManualPositionDetectedEvent()
    {
        var (svc, _, _, reconciliationEvents) = BuildService();

        await svc.DetectNewManualPositionsAsync(
            [StockPos("AMD", 5)], new OrdersSnapshot([], false), CancellationToken.None);

        reconciliationEvents.Verify(r => r.SaveAsync(
            It.Is<ReconciliationEvent>(e =>
                e.EventType == "ManualPositionDetected" &&
                e.Symbol    == "AMD"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanClosedManualPositions_WhenManualPositionNoLongerInIbkr_LogsManualPositionClosedEvent()
    {
        var (svc, _, repo, reconciliationEvents) = BuildService();
        var manual = ManualStockDbPosition("SPX", "MANUAL-SPX-1784295727968");
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([manual]);

        // IBKR reports no positions at all — the manual SPX row is gone.
        await svc.CleanClosedManualPositionsAsync([], CancellationToken.None);

        reconciliationEvents.Verify(r => r.SaveAsync(
            It.Is<ReconciliationEvent>(e =>
                e.EventType == "ManualPositionClosed" &&
                e.Symbol    == "SPX" &&
                e.OrderId   == "MANUAL-SPX-1784295727968"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- BuildManualPosition parity with StartupReconciliationService (entry_price/entry_amount) --

    // Both reconciliation services independently build a manual OpenPosition from the same
    // IbkrPosition shape. They must never disagree on the options /100 avgCost scaling again
    // (see the 2026-07-17/18 MANUAL-SPX and MANUAL-UBER incidents, where this service stored
    // the raw, unscaled avgCost as entry_price — 100x too high, and entry_amount 10000x too
    // high). Both BuildManualPosition methods are private static, so this reflects into them
    // directly rather than exercising the full detection pipeline, matching the reflection
    // pattern already used elsewhere in this suite (see IbkrBrokerServiceTests).
    [Fact]
    public void BuildManualPosition_OptionsContract_MatchesStartupReconciliationServiceEntryPriceAndAmount()
    {
        // Real shape from the 2026-07-17/18 MANUAL-UBER incident: IBKR's avgCost for options
        // is per-contract (already x100), quantity 4, true per-share premium ~$4.906833.
        var ibkrPos = new IbkrPosition("UBER", "OPT", "UBER270617C00100000", 4, 490.6833m);

        var periodicMethod = typeof(PeriodicReconciliationService).GetMethod(
            "BuildManualPosition", BindingFlags.NonPublic | BindingFlags.Static)!;
        var startupMethod = typeof(StartupReconciliationService).GetMethod(
            "BuildManualPosition", BindingFlags.NonPublic | BindingFlags.Static)!;

        var periodicResult = (OpenPosition)periodicMethod.Invoke(null, [ibkrPos, null])!;
        var startupResult  = (OpenPosition)startupMethod.Invoke(null, [ibkrPos, null])!;

        periodicResult.EntryPrice.Should().Be(startupResult.EntryPrice);
        periodicResult.EntryAmount.Should().Be(startupResult.EntryAmount);

        // And both must reflect the true, /100-adjusted per-share economics, not the raw avgCost.
        periodicResult.EntryPrice.Should().Be(4.906833m);
        periodicResult.EntryAmount.Should().Be(1962.7332m);
    }

    [Fact]
    public void BuildManualPosition_StockPosition_MatchesStartupReconciliationServiceEntryPriceAndAmount()
    {
        // Stocks need no /100 adjustment on either side — confirms the fix didn't disturb that path.
        var ibkrPos = new IbkrPosition("TSLA", "STK", null, 10, 250.00m);

        var periodicMethod = typeof(PeriodicReconciliationService).GetMethod(
            "BuildManualPosition", BindingFlags.NonPublic | BindingFlags.Static)!;
        var startupMethod = typeof(StartupReconciliationService).GetMethod(
            "BuildManualPosition", BindingFlags.NonPublic | BindingFlags.Static)!;

        var periodicResult = (OpenPosition)periodicMethod.Invoke(null, [ibkrPos, null])!;
        var startupResult  = (OpenPosition)startupMethod.Invoke(null, [ibkrPos, null])!;

        periodicResult.EntryPrice.Should().Be(startupResult.EntryPrice).And.Be(250.00m);
        periodicResult.EntryAmount.Should().Be(startupResult.EntryAmount).And.Be(2500.00m);
    }
}
