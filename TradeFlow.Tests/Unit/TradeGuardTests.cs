using Microsoft.Extensions.Logging.Abstractions;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Unit;

public class TradeGuardTests
{
    private readonly Mock<IBrokerService> _brokerMock = new();
    private readonly TradeGuard _guard;

    public TradeGuardTests()
    {
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(default))
            .ReturnsAsync(100_000m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(default))
            .ReturnsAsync(0m);

        _guard = new TradeGuard(_brokerMock.Object, NullLogger<TradeGuard>.Instance);
    }

    private static TradeOrder BuildOrder(
        string symbol = "TSLA",
        string? contractSymbol = "TSLA260620C00450000",
        decimal budgetUsed = 1_000m,
        bool isAverage = false) =>
        new(
            AlertId: Guid.NewGuid().ToString(),
            UserName: "TestTrader",
            Symbol: symbol,
            TradeType: TradeType.Options,
            OptionsContractSymbol: contractSymbol,
            Direction: "call",
            Strike: 450,
            Expiration: "2026-06-20",
            Quantity: 2,
            EstimatedEntryPrice: 4.95m,
            BudgetUsed: budgetUsed,
            StopPrice: 2.48m,
            TargetPrice: 14.85m,
            IsAverage: isAverage);

    private static BrokerOrderResult BuildResult(string orderId = "ORDER-001") =>
        new(
            OrderId: orderId,
            StopOrderId: "STOP-001",
            TargetOrderId: "TGT-001",
            FillPrice: 4.95m,
            FillQuantity: 2,
            FillAmount: 990m,
            Status: OrderStatus.Filled,
            FilledAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task CheckAsync_AllowsValidOrder()
    {
        var order = BuildOrder();
        var result = await _guard.CheckAsync(order);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_BlocksDuplicateOpenPosition()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        // Try to open the same position again
        var result = await _guard.CheckAsync(order);

        result.Should().NotBeNull();
        result.Should().Contain("already open");
    }

    [Fact]
    public async Task CheckAsync_BlocksWhenExposureExceedsBalance()
    {
        // Set up broker to return very low available balance
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(default))
            .ReturnsAsync(100m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(default))
            .ReturnsAsync(95m);

        var order = BuildOrder(budgetUsed: 1_000m);
        var result = await _guard.CheckAsync(order);

        result.Should().NotBeNull();
        result.Should().Contain("Insufficient");
    }

    [Fact]
    public async Task CheckAsync_AllowsAveragingWhenNotYetAveraged()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var avgOrder = BuildOrder(isAverage: true);
        var result = await _guard.CheckAsync(avgOrder);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_BlocksSecondAverage()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        // First average
        var avgOrder = BuildOrder(isAverage: true);
        _guard.RegisterOpen(avgOrder, BuildResult("ORDER-002"));

        // Second average should be blocked
        var result = await _guard.CheckAsync(avgOrder);

        result.Should().NotBeNull();
        result.Should().Contain("Already averaged");
    }

    [Fact]
    public void RegisterClose_PopulatesExitData()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var closed = _guard.RegisterClose(
            "TestTrader", "TSLA260620C00450000", "TSLA", 8.20m, TradeOutcome.XtradesExit);

        closed.Should().NotBeNull();
        closed!.ExitPrice.Should().Be(8.20m);
        closed.PnL.Should().BePositive();
        closed.Result.Should().Be(TradeOutcome.XtradesExit);
        closed.Status.Should().Be(TradeStatus.Closed);
    }

    [Fact]
    public void GetDailyTradeCount_IncrementsOnRegisterOpen()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        _guard.GetDailyTradeCount().Should().Be(1);
    }
}