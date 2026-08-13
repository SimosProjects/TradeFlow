using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Vela.Worker.Data;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

/// <summary>
/// Unit tests for GhostPositionCloseOutService. IBrokerService and ITradeMetricsRepository
/// are mocked via Moq; CsvTradeLogger is a real instance writing to a temp directory,
/// matching the pattern in CsvTradeLoggerTests.
/// </summary>
public class GhostPositionCloseOutServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CsvTradeLogger _csv;

    public GhostPositionCloseOutServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vela_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Trades:Directory"] = _tempDir })
            .Build();

        _csv = new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -- Helpers --

    private static OpenPosition StockPosition(string orderId = "9001", DateTimeOffset? lastVerifiedOpenAt = null) =>
        new()
        {
            OrderId            = orderId,
            AlertId            = "alert-1",
            UserName           = "Fibonaccizer",
            Symbol             = "TSLA",
            TradeType          = "Stock",
            Quantity           = 10,
            EntryPrice         = 200m,
            EntryAmount        = 2000m,
            StopPrice          = 180m,
            TargetPrice        = 240m,
            OpenedAt           = DateTimeOffset.UtcNow,
            IsManual           = false,
            LastVerifiedOpenAt = lastVerifiedOpenAt,
        };

    private static TradeMetric Metric(string orderId = "9001") =>
        new()
        {
            Id          = orderId,
            AlertId     = "alert-1",
            Symbol      = "TSLA",
            TradeType   = "Stock",
            Quantity    = 10,
            EntryAmount = 2000m,
            XScore      = 61m,
            DiscordRank = "Gold",
        };

    // -- CloseOutAsync --

    [Fact]
    public async Task CloseOutAsync_WhenQuoteLookupSucceeds_ClosesTradeMetricsAndCsvWithComputedPnL()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetCurrentMarketPriceAsync(
                "TSLA", TradeType.Stock, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(220m);

        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync("9001", It.IsAny<CancellationToken>()))
               .ReturnsAsync(Metric());

        decimal? capturedExitPrice = null;
        decimal? capturedPnl = null;
        decimal? capturedPnlPct = null;
        metrics.Setup(m => m.CloseAsync(
                "9001", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                "ClosedExternally", It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()))
               .Callback<string, decimal?, decimal?, decimal?, decimal?, string, DateTimeOffset, int?, decimal?, CancellationToken>(
                   (_, exitPrice, _, pnl, pnlPct, _, _, _, _, _) =>
                   {
                       capturedExitPrice = exitPrice;
                       capturedPnl       = pnl;
                       capturedPnlPct    = pnlPct;
                   })
               .Returns(Task.CompletedTask);

        var svc = new GhostPositionCloseOutService(
            broker.Object, metrics.Object, _csv, NullLogger<GhostPositionCloseOutService>.Instance);

        await svc.CloseOutAsync(StockPosition());

        // Exit amount = 220 * 10 = 2200; P&L = 2200 - 2000 = 200; P&L% = 200 / 2000 * 100 = 10%
        capturedExitPrice.Should().Be(220m);
        capturedPnl.Should().Be(200m);
        capturedPnlPct.Should().Be(10m);

        metrics.Verify(m => m.CloseAsync(
            "9001", 220m, 2200m, 200m, 10m, "ClosedExternally",
            It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()), Times.Once);

        var stocksCsv = await File.ReadAllTextAsync(Path.Combine(_tempDir, "stocks_trades.csv"));
        stocksCsv.Should().Contain("TSLA");
        stocksCsv.Should().Contain("Closed");
        stocksCsv.Should().Contain("220.00");
    }

    [Fact]
    public async Task CloseOutAsync_WhenQuoteLookupTimesOut_ClosesTradeMetricsWithNullExitData()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetCurrentMarketPriceAsync(
                "TSLA", TradeType.Stock, null, null, null, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException("quote request timed out"));

        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync("9001", It.IsAny<CancellationToken>()))
               .ReturnsAsync(Metric());
        metrics.Setup(m => m.CloseAsync(
                It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                It.IsAny<decimal?>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<int?>(),
                It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        var svc = new GhostPositionCloseOutService(
            broker.Object, metrics.Object, _csv, NullLogger<GhostPositionCloseOutService>.Instance);

        // Must not throw — a failed quote lookup must never block the caller's DeleteAsync.
        await svc.Invoking(s => s.CloseOutAsync(StockPosition())).Should().NotThrowAsync();

        metrics.Verify(m => m.CloseAsync(
            "9001",
            null, null, null, null,
            "ClosedExternally",
            It.IsAny<DateTimeOffset>(), null, null,
            It.IsAny<CancellationToken>()), Times.Once);

        var stocksCsv = await File.ReadAllTextAsync(Path.Combine(_tempDir, "stocks_trades.csv"));
        stocksCsv.Should().Contain("TSLA");
        stocksCsv.Should().Contain("Closed");
    }

    // -- Exit price waterfall (tier 1: reqExecutions, tier 2: anchored intraday bars, tier 3: live quote) --

    [Fact]
    public async Task TryGetExitPrice_WhenExecutionHistoryFindsFill_UsesExactExecutionPriceAndSkipsLowerTiers()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetRecentExecutionAsync(
                "TSLA", TradeType.Stock, null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new BrokerExecution(225m, DateTimeOffset.UtcNow.AddMinutes(-10)));

        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync("9001", It.IsAny<CancellationToken>()))
               .ReturnsAsync(Metric());

        decimal? capturedExitPrice = null;
        metrics.Setup(m => m.CloseAsync(
                "9001", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                "ClosedExternally", It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()))
               .Callback<string, decimal?, decimal?, decimal?, decimal?, string, DateTimeOffset, int?, decimal?, CancellationToken>(
                   (_, exitPrice, _, _, _, _, _, _, _, _) => capturedExitPrice = exitPrice)
               .Returns(Task.CompletedTask);

        var svc = new GhostPositionCloseOutService(
            broker.Object, metrics.Object, _csv, NullLogger<GhostPositionCloseOutService>.Instance);

        await svc.CloseOutAsync(StockPosition());

        capturedExitPrice.Should().Be(225m);
        broker.Verify(b => b.GetIntradayBarsAsync(
            It.IsAny<string>(), It.IsAny<TradeType>(), It.IsAny<string?>(), It.IsAny<decimal?>(),
            It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(b => b.GetCurrentMarketPriceAsync(
            It.IsAny<string>(), It.IsAny<TradeType>(), It.IsAny<string?>(), It.IsAny<decimal?>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryGetExitPrice_WhenExecutionMissesButAnchorExists_UsesAnchoredIntradayBarAndSkipsLiveQuote()
    {
        var anchor = DateTimeOffset.UtcNow.AddMinutes(-45);

        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetRecentExecutionAsync(
                "TSLA", TradeType.Stock, null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync((BrokerExecution?)null);
        broker.Setup(b => b.GetIntradayBarsAsync(
                "TSLA", TradeType.Stock, null, null, null, anchor, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<IntradayBar>
              {
                  new(anchor.AddMinutes(10), 221m, 223m, 220m, 222m, 1000),
                  new(anchor.AddMinutes(20), 222m, 224m, 221m, 223.50m, 1200), // most recent — expected
              });

        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync("9001", It.IsAny<CancellationToken>()))
               .ReturnsAsync(Metric());

        decimal? capturedExitPrice = null;
        metrics.Setup(m => m.CloseAsync(
                "9001", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                "ClosedExternally", It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()))
               .Callback<string, decimal?, decimal?, decimal?, decimal?, string, DateTimeOffset, int?, decimal?, CancellationToken>(
                   (_, exitPrice, _, _, _, _, _, _, _, _) => capturedExitPrice = exitPrice)
               .Returns(Task.CompletedTask);

        var svc = new GhostPositionCloseOutService(
            broker.Object, metrics.Object, _csv, NullLogger<GhostPositionCloseOutService>.Instance);

        await svc.CloseOutAsync(StockPosition(lastVerifiedOpenAt: anchor));

        capturedExitPrice.Should().Be(223.50m);
        broker.Verify(b => b.GetCurrentMarketPriceAsync(
            It.IsAny<string>(), It.IsAny<TradeType>(), It.IsAny<string?>(), It.IsAny<decimal?>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryGetExitPrice_WhenExecutionAndBarsBothMiss_FallsBackToLiveQuote()
    {
        var anchor = DateTimeOffset.UtcNow.AddMinutes(-45);

        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetRecentExecutionAsync(
                "TSLA", TradeType.Stock, null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync((BrokerExecution?)null);
        broker.Setup(b => b.GetIntradayBarsAsync(
                "TSLA", TradeType.Stock, null, null, null, anchor, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<IntradayBar>());
        broker.Setup(b => b.GetCurrentMarketPriceAsync(
                "TSLA", TradeType.Stock, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(219m);

        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync("9001", It.IsAny<CancellationToken>()))
               .ReturnsAsync(Metric());

        decimal? capturedExitPrice = null;
        metrics.Setup(m => m.CloseAsync(
                "9001", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                "ClosedExternally", It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()))
               .Callback<string, decimal?, decimal?, decimal?, decimal?, string, DateTimeOffset, int?, decimal?, CancellationToken>(
                   (_, exitPrice, _, _, _, _, _, _, _, _) => capturedExitPrice = exitPrice)
               .Returns(Task.CompletedTask);

        var svc = new GhostPositionCloseOutService(
            broker.Object, metrics.Object, _csv, NullLogger<GhostPositionCloseOutService>.Instance);

        await svc.CloseOutAsync(StockPosition(lastVerifiedOpenAt: anchor));

        capturedExitPrice.Should().Be(219m);
    }

    [Fact]
    public async Task TryGetExitPrice_WhenNoAnchorExists_SkipsIntradayBarsAndGoesStraightToLiveQuote()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetRecentExecutionAsync(
                "TSLA", TradeType.Stock, null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync((BrokerExecution?)null);
        broker.Setup(b => b.GetCurrentMarketPriceAsync(
                "TSLA", TradeType.Stock, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(218m);

        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync("9001", It.IsAny<CancellationToken>()))
               .ReturnsAsync(Metric());

        decimal? capturedExitPrice = null;
        metrics.Setup(m => m.CloseAsync(
                "9001", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                "ClosedExternally", It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()))
               .Callback<string, decimal?, decimal?, decimal?, decimal?, string, DateTimeOffset, int?, decimal?, CancellationToken>(
                   (_, exitPrice, _, _, _, _, _, _, _, _) => capturedExitPrice = exitPrice)
               .Returns(Task.CompletedTask);

        var svc = new GhostPositionCloseOutService(
            broker.Object, metrics.Object, _csv, NullLogger<GhostPositionCloseOutService>.Instance);

        // StockPosition() defaults LastVerifiedOpenAt to null — never persisted an anchor.
        await svc.CloseOutAsync(StockPosition());

        capturedExitPrice.Should().Be(218m);
        broker.Verify(b => b.GetIntradayBarsAsync(
            It.IsAny<string>(), It.IsAny<TradeType>(), It.IsAny<string?>(), It.IsAny<decimal?>(),
            It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseOutAsync_WhenNoTradeMetricRowExists_DoesNothing()
    {
        var broker = new Mock<IBrokerService>();
        var metrics = new Mock<ITradeMetricsRepository>();
        metrics.Setup(m => m.GetByOrderIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((TradeMetric?)null);

        var svc = new GhostPositionCloseOutService(
            broker.Object, metrics.Object, _csv, NullLogger<GhostPositionCloseOutService>.Instance);

        // Manual/reconciliation-synthesized positions never have a trade_metrics row.
        await svc.CloseOutAsync(StockPosition());

        broker.Verify(b => b.GetCurrentMarketPriceAsync(
            It.IsAny<string>(), It.IsAny<TradeType>(), It.IsAny<string?>(), It.IsAny<decimal?>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        metrics.Verify(m => m.CloseAsync(
            It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<decimal?>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<int?>(),
            It.IsAny<decimal?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
