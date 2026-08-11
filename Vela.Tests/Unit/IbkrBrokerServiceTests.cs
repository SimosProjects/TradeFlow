using System.Reflection;
using IBApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class IbkrBrokerServiceTests
{
    // SyncOrderId/SyncReqId and NextReqId() don't require a live Gateway connection —
    // construction alone (no Connect()) is enough, same pattern as
    // IbkrConnectionTests.BuildBrokerService. NextReqId() is private, so its return value
    // is observed via reflection rather than through a public call path that would
    // otherwise require EnsureConnected() to be true.
    [Fact]
    public void SyncReqId_SeedsNextReqIdWellClearOfDefault()
    {
        var options = Options.Create(new IbkrOptions
        {
            Host      = "127.0.0.1",
            Port      = 9999,
            ClientId  = 99,
            AccountId = "",
            TimeoutMs = 2000
        });

        var connection = new IbkrConnectionService(
            options,
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance,
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance));

        var broker = new IbkrBrokerService(connection, options, NullLogger<IbkrBrokerService>.Instance);

        broker.SyncReqId(50);

        var nextReqIdMethod = typeof(IbkrBrokerService).GetMethod(
            "NextReqId", BindingFlags.NonPublic | BindingFlags.Instance);
        var nextReqId = (int)nextReqIdMethod!.Invoke(broker, null)!;

        nextReqId.Should().Be(50 + 100_000 + 1);

        connection.Dispose();
    }

    // No live Gateway means GetAllOpenOrdersAsync (called internally by
    // ReRegisterStopCallbacksAsync) returns an empty snapshot — every stored order ID is
    // therefore "not found live" by construction, exercising the stale-ID path without
    // needing to fake an actual open orders response.
    [Fact]
    public async Task ReRegisterStopCallbacksAsync_StopOrderIdNotInLiveSnapshot_NotRegisteredAndWarns()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();

        var position = new OpenPosition
        {
            OrderId       = "10768",
            StopOrderId   = "11241",
            TargetOrderId = null,
            Symbol        = "BROS"
        };

        await broker.ReRegisterStopCallbacksAsync([position]);

        broker.IsKnownOrder(11241).Should().BeFalse();
        logger.Warnings.Should().ContainSingle(w => w.Contains("BROS") && w.Contains("11241"));

        connection.Dispose();
    }

    // ClassifyStaleOrderId is private, so its (Live, Trackable) tuple is observed via
    // reflection, same pattern as SyncReqId_SeedsNextReqIdWellClearOfDefault. This is the
    // exact tri-state distinction requested: a live OrderId-0 match must not be conflated
    // with either "stale" (genuinely no live order) or "fully tracked" (real ID to register).
    [Fact]
    public void ClassifyStaleOrderId_LiveOrderIdZeroMatch_IsLiveButNotTrackable_AndDoesNotLogStale()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();

        var liveOrders = new List<IbkrOpenOrder>
        {
            new(0, "GE", "STK", null, "SELL", "TRAIL", 11, "PreSubmitted", 305.91, null)
        };

        var method = typeof(IbkrBrokerService).GetMethod(
            "ClassifyStaleOrderId", BindingFlags.NonPublic | BindingFlags.Instance);
        var (live, trackable) = ((bool, bool))method!.Invoke(
            broker, [ "GE", "Stop", 11252, "GE", liveOrders, true ])!;

        live.Should().BeTrue();
        trackable.Should().BeFalse();
        logger.Warnings.Should().ContainSingle(w => w.Contains("OrderId 0") && w.Contains("GE"));
        logger.Warnings.Should().NotContain(w => w.Contains("stale", StringComparison.OrdinalIgnoreCase));

        connection.Dispose();
    }

    [Fact]
    public void ClassifyStaleOrderId_NoLiveMatch_IsStale_AndLogsStaleMessage()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();

        var method = typeof(IbkrBrokerService).GetMethod(
            "ClassifyStaleOrderId", BindingFlags.NonPublic | BindingFlags.Instance);
        var (live, trackable) = ((bool, bool))method!.Invoke(
            broker, [ "GE", "Stop", 11252, "GE", new List<IbkrOpenOrder>(), true ])!;

        live.Should().BeFalse();
        trackable.Should().BeFalse();
        logger.Warnings.Should().ContainSingle(w => w.Contains("stale", StringComparison.OrdinalIgnoreCase));

        connection.Dispose();
    }

    // -- PlaceTrailWithTargetAsync OCA target-leg rejection detection (2026-08-04 MPC incident) --

    [Fact]
    public async Task PlaceTrailWithTargetAsync_TargetLegRejected_FallsBackAndReturnsNullTarget()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();
        var contract = new Contract { Symbol = "MPC", SecType = "STK" };

        var method = typeof(IbkrBrokerService).GetMethod(
            "PlaceTrailWithTargetAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        var task = (Task<(string? StopId, string? TargetId)>)method!.Invoke(
            broker, ["MPC", "15381", 15383, 15384, contract, 12, 5.0, 325.985m, CancellationToken.None])!;

        // Give the method a moment to register both rejection watchers before simulating
        // IBKR's [110] rejection on the target leg — the exact failure from the MPC incident,
        // where nothing was watching for a rejection on this specific order ID.
        await Task.Delay(50);
        connection.Wrapper.error(
            15384, 110, "The price does not conform to the minimum price variation for this contract.");

        var (stopId, targetId) = await task;

        targetId.Should().BeNull();
        stopId.Should().NotBeNull();
        logger.Warnings.Should().ContainSingle(w =>
            w.Contains("falling back to trail-only") && w.Contains("MPC"));
        logger.Debugs.Should().NotContain(d => d.Contains("confirmed live"));

        connection.Dispose();
    }

    [Fact]
    public async Task PlaceTrailWithTargetAsync_StopLegRejected_FallsBackAndReturnsNullTarget()
    {
        // Regression check: the rewrite from a single rejection watcher to Task.WhenAny over
        // two must not break the stop-leg rejection path, which already worked before this fix.
        var (broker, connection, logger) = BuildDisconnectedBroker();
        var contract = new Contract { Symbol = "TEST", SecType = "STK" };

        var method = typeof(IbkrBrokerService).GetMethod(
            "PlaceTrailWithTargetAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        var task = (Task<(string? StopId, string? TargetId)>)method!.Invoke(
            broker, ["TEST", "20001", 20003, 20004, contract, 10, 5.0, 100.00m, CancellationToken.None])!;

        await Task.Delay(50);
        connection.Wrapper.error(20003, 201, "Order rejected - reason:margin insufficient");

        var (stopId, targetId) = await task;

        targetId.Should().BeNull();
        logger.Warnings.Should().ContainSingle(w => w.Contains("falling back to trail-only"));
        logger.Debugs.Should().NotContain(d => d.Contains("confirmed live"));

        connection.Dispose();
    }

    [Fact]
    public async Task PlaceTrailWithTargetAsync_NeitherLegRejected_ReturnsBothIdsAndLogsConfirmedLive()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();
        var contract = new Contract { Symbol = "TEST", SecType = "STK" };

        var method = typeof(IbkrBrokerService).GetMethod(
            "PlaceTrailWithTargetAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        var task = (Task<(string? StopId, string? TargetId)>)method!.Invoke(
            broker, ["TEST", "20001", 20003, 20004, contract, 10, 5.0, 100.00m, CancellationToken.None])!;

        var (stopId, targetId) = await task;

        stopId.Should().Be("20003");
        targetId.Should().Be("20004");
        logger.Debugs.Should().ContainSingle(d => d.Contains("confirmed live") && d.Contains("TEST"));
        logger.Warnings.Should().BeEmpty();

        connection.Dispose();
    }

    // -- BuildOcaLimitOrder tick rounding (the root cause of the MPC incident) --

    [Fact]
    public void BuildOcaLimitOrder_UnroundedPrice_RoundsToNearestCent()
    {
        var method = typeof(IbkrBrokerService).GetMethod(
            "BuildOcaLimitOrder", BindingFlags.NonPublic | BindingFlags.Static);

        var order = (Order)method!.Invoke(null, [15384, 12, 325.987m, "OCA_TEST"])!;

        order.LmtPrice.Should().Be(325.99);
    }

    [Fact]
    public void BuildOcaLimitOrder_TheExactMpcIncidentPrice_NoLongerSubmitsAnInvalidTick()
    {
        // The exact unrounded target price from the 2026-08-04 MPC incident (OrderId 15384) —
        // three decimal places is not a valid $0.01 stock tick and IBKR rejected it with [110].
        var method = typeof(IbkrBrokerService).GetMethod(
            "BuildOcaLimitOrder", BindingFlags.NonPublic | BindingFlags.Static);

        var order = (Order)method!.Invoke(null, [15384, 12, 325.985m, "OCA_TEST"])!;

        order.LmtPrice.Should().NotBe(325.985);
        (order.LmtPrice * 100).Should().BeApproximately(Math.Round(order.LmtPrice * 100), 0.0001);
    }

    // -- BuildRejectedCloseResult (the 2026-08-10 TKO incident) --

    [Fact]
    public void BuildRejectedCloseResult_InactiveStatus_ReturnsRejectedNotFilled()
    {
        var (broker, connection, _) = BuildDisconnectedBroker();

        var method = typeof(IbkrBrokerService).GetMethod(
            "BuildRejectedCloseResult", BindingFlags.NonPublic | BindingFlags.Instance);

        // The exact TKO shape: a market close order IBKR reported Inactive, zero fill.
        var fill = new OrderFill("Inactive", 0m, 0);
        var result = (BrokerOrderResult)method!.Invoke(
            broker, ["TKO", TradeType.Stock, 15864, 13, fill, null])!;

        result.Status.Should().Be(OrderStatus.Rejected);
        result.FillQuantity.Should().Be(0);
        result.FillPrice.Should().Be(0m);

        connection.Dispose();
    }

    [Fact]
    public void BuildRejectedCloseResult_CancelledStatus_ReturnsCancelledNotFilled()
    {
        var (broker, connection, _) = BuildDisconnectedBroker();

        var method = typeof(IbkrBrokerService).GetMethod(
            "BuildRejectedCloseResult", BindingFlags.NonPublic | BindingFlags.Instance);

        var fill = new OrderFill("Cancelled", 0m, 0);
        var result = (BrokerOrderResult)method!.Invoke(
            broker, ["TEST", TradeType.Stock, 20001, 10, fill, null])!;

        result.Status.Should().Be(OrderStatus.Cancelled);
        result.FillQuantity.Should().Be(0);

        connection.Dispose();
    }

    [Fact]
    public void BuildRejectedCloseResult_ReadsDetailedReasonWhenIbkrProvidedOne()
    {
        var (broker, connection, _) = BuildDisconnectedBroker();

        // The actual IBKR error for TKO, arriving on the same order id shortly after the
        // Inactive orderStatus callback.
        connection.Wrapper.error(15864, 201, "Order rejected - reason:Exchange is closed.");

        var method = typeof(IbkrBrokerService).GetMethod(
            "BuildRejectedCloseResult", BindingFlags.NonPublic | BindingFlags.Instance);
        var fill = new OrderFill("Inactive", 0m, 0);
        var result = (BrokerOrderResult)method!.Invoke(
            broker, ["TKO", TradeType.Stock, 15864, 13, fill, null])!;

        result.RejectionReason.Should().Contain("Exchange is closed");

        connection.Dispose();
    }

    [Fact]
    public void BuildRejectedCloseResult_WithGenuinePartialFill_ReturnsPartialFillNotFullQuantity()
    {
        var (broker, connection, _) = BuildDisconnectedBroker();

        var method = typeof(IbkrBrokerService).GetMethod(
            "BuildRejectedCloseResult", BindingFlags.NonPublic | BindingFlags.Instance);

        // 6 of 13 confirmed sold before the remainder went inactive — the 2026-07-17 UBER
        // incident is exactly this shape: never report the full requested quantity.
        var fill = new OrderFill("Inactive", 0m, 0);
        var partial = new OrderFill("Filled", 187.00m, 6);
        var result = (BrokerOrderResult)method!.Invoke(
            broker, ["TKO", TradeType.Stock, 15864, 13, fill, partial])!;

        result.Status.Should().Be(OrderStatus.PartialFill);
        result.FillQuantity.Should().Be(6);
        result.FillPrice.Should().Be(187.00m);

        connection.Dispose();
    }

    // -- GetAllPositionsAsync connectivity gap (2026-08-10 TKO / 2026-08-11 MGY investigation) --

    [Fact]
    public async Task GetAllPositionsAsync_NotConnected_ReturnsTimedOutTrueNotConfirmedEmpty()
    {
        // Before the fix this returned PositionsSnapshot([], false) — indistinguishable from
        // a genuine, successfully-queried empty account, which VerifyCloseExecutedAsync would
        // read as "position confirmed absent, close executed."
        var (broker, connection, _) = BuildDisconnectedBroker();

        var snapshot = await broker.GetAllPositionsAsync();

        snapshot.TimedOut.Should().BeTrue();
        snapshot.Positions.Should().BeEmpty();

        connection.Dispose();
    }

    // -- GetAllOpenOrdersAsync connectivity gap (same shape, found while fixing GetAllPositionsAsync) --

    [Fact]
    public async Task GetAllOpenOrdersAsync_NotConnected_ReturnsTimedOutTrueNotConfirmedEmpty()
    {
        // Same hazard as GetAllPositionsAsync — a connection failure must not be reported the
        // same way as a genuine, successfully-queried empty open-orders snapshot. Callers that
        // gate on TimedOut (StartupReconciliationService.ClassifyOpenOrdersAsync, Vela.Guardian's
        // ConfirmOrderIsLiveAsync) must be told this was not confirmed.
        var (broker, connection, _) = BuildDisconnectedBroker();

        var snapshot = await broker.GetAllOpenOrdersAsync();

        snapshot.TimedOut.Should().BeTrue();
        snapshot.Orders.Should().BeEmpty();

        connection.Dispose();
    }

    private static (IbkrBrokerService Broker, IbkrConnectionService Connection, CapturingLogger<IbkrBrokerService> Logger)
        BuildDisconnectedBroker()
    {
        var options = Options.Create(new IbkrOptions
        {
            Host      = "127.0.0.1",
            Port      = 9999,
            ClientId  = 99,
            AccountId = "",
            TimeoutMs = 2000
        });

        var connection = new IbkrConnectionService(
            options,
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance,
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance));

        var logger = new CapturingLogger<IbkrBrokerService>();
        var broker = new IbkrBrokerService(connection, options, logger);

        return (broker, connection, logger);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = new();
        public List<string> Debugs { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
            else if (logLevel == LogLevel.Debug)
                Debugs.Add(formatter(state, exception));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
