using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Unit;

public class BrokerExecutionServiceTests
{
    private readonly Mock<IBrokerService> _brokerMock = new();
    private readonly TradeGuard _guard;
    private readonly PositionSizer _sizer = new();
    private readonly BrokerExecutionService _execution;

    public BrokerExecutionServiceTests()
    {
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(default))
            .ReturnsAsync(100_000m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(default))
            .ReturnsAsync(0m);
        _brokerMock.Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "ORDER-001",
                StopOrderId: "STOP-001",
                TargetOrderId: "TGT-001",
                FillPrice: 4.95m,
                FillQuantity: 2,
                FillAmount: 990m,
                Status: OrderStatus.Filled,
                FilledAt: DateTimeOffset.UtcNow));

        _guard = new TradeGuard(_brokerMock.Object, NullLogger<TradeGuard>.Instance);

        var config = new ConfigurationBuilder().Build();

        _execution = new BrokerExecutionService(
            _brokerMock.Object,
            _sizer,
            _guard,
            new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance),
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            NullLogger<BrokerExecutionService>.Instance);
    }

    private static Alert BuildAlert(
        string side = "bto",
        string type = "options",
        string direction = "call",
        decimal? pricePaid = 4.95m,
        string? contractSymbol = "TSLA260620C00450000",
        decimal? strike = 450) =>
        new(
            Id: Guid.NewGuid().ToString(),
            UserId: null,
            UserName: "TestTrader",
            Symbol: "TSLA",
            Type: type,
            Direction: direction,
            Strike: strike,
            Expiration: "2026-06-20T00:00:00",
            OptionsContractSymbol: contractSymbol,
            ContractDescription: null,
            Side: side,
            Status: "open",
            Result: null,
            ActualPriceAtTimeOfAlert: pricePaid,
            ActualPriceAtTimeOfExit: null,
            PricePaid: pricePaid,
            PriceAtExit: null,
            HighestPrice: null,
            LowestPrice: null,
            LastCheckedPrice: null,
            Risk: "standard",
            LastKnownPercentProfit: null,
            IsProfitableTrade: null,
            XScore: 80,
            CanAverage: true,
            TimeOfEntryAlert: null,
            TimeOfFullExitAlert: null,
            FormattedLength: null,
            IsSwing: false,
            IsBullish: true,
            IsShort: false,
            Strategy: null,
            OriginalMessage: null,
            OriginalExitMessage: null);

    private static AlertClassification CallClassification() =>
        new(AlertCategory.CallOptionEntry, "Call option entry");

    [Fact]
    public async Task HandleEntryAsync_SkipsWhenMarketClosed()
    {
        // BrokerExecutionService checks IsMarketOpen() before placing any order.
        // Since tests run outside market hours, no order should be placed.
        var alert = BuildAlert();
        var classification = CallClassification();

        await _execution.HandleEntryAsync(alert, classification);

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_SkipsWhenPriceMissing()
    {
        var alert = BuildAlert(pricePaid: null);
        var classification = CallClassification();

        await _execution.HandleEntryAsync(alert, classification);

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleExitAsync_SkipsWhenNoOpenPosition()
    {
        var alert = BuildAlert(side: "stc");

        await _execution.HandleExitAsync(alert);

        _brokerMock.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default), Times.Never);
    }
}