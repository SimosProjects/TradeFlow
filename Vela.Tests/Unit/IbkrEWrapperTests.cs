using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class IbkrEWrapperTests
{
    [Fact]
    public async Task OpenOrder_TrailOrder_UsesTrailStopPriceNotAuxPrice()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterAllOpenOrdersCallback();

        var contract = new Contract { Symbol = "NVDA", SecType = "STK" };
        var order = new Order
        {
            Action = "SELL",
            OrderType = "TRAIL",
            TotalQuantity = 100,
            AuxPrice = double.MaxValue,
            TrailStopPrice = 166.50,
            LmtPrice = double.MaxValue
        };
        var orderState = new OrderState { Status = "Submitted" };

        wrapper.openOrder(1, contract, order, orderState);
        wrapper.openOrderEnd();

        var orders = await tcs.Task;
        var mapped = Assert.Single(orders);
        Assert.Equal(166.50, mapped.AuxPrice);
    }

    [Fact]
    public async Task OpenOrder_PlainStopOrder_StillUsesAuxPrice()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterAllOpenOrdersCallback();

        var contract = new Contract { Symbol = "AAPL", SecType = "STK" };
        var order = new Order
        {
            Action = "SELL",
            OrderType = "STP",
            TotalQuantity = 50,
            AuxPrice = 200.00,
            TrailStopPrice = double.MaxValue,
            LmtPrice = double.MaxValue
        };
        var orderState = new OrderState { Status = "Submitted" };

        wrapper.openOrder(2, contract, order, orderState);
        wrapper.openOrderEnd();

        var orders = await tcs.Task;
        var mapped = Assert.Single(orders);
        Assert.Equal(200.00, mapped.AuxPrice);
    }

    // PlaceProtectiveStopAsync (and PlaceTrailWithFallbackAsync/PlaceTrailWithTargetAsync)
    // can't be exercised end-to-end here — EnsureConnected() requires a real live socket to
    // Gateway, same reason IbkrBrokerServiceTests don't exist and IbkrConnectionTests are
    // gated behind SKIP_IBKR_TESTS. This instead verifies the actual mechanism the 103 fix
    // touches: error() must resolve a registered _stopRejectionCallbacks entry for code 103,
    // exactly as it already does for 201 and 404, so callers waiting on that TCS see the
    // rejection within their detection window instead of timing out into a false "accepted".
    [Fact]
    public async Task Error_DuplicateOrderId103_ResolvesStopRejectionCallback()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        wrapper.RegisterStopRejectionCallback(42, tcs);

        wrapper.error(42, 103, "Duplicate order id");

        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("Duplicate order id", await tcs.Task);
    }

    // Reproduces the SPX/SPXW incident (2026-07-17/18): the alert-supplied contract said
    // "SPX260717C07500000" but IBKR resolved and filled the order against its actual
    // weekly listing, "SPXW260717C07500000". execDetails receives the full Contract IBKR
    // actually executed against, LocalSymbol included — this confirms that value now flows
    // into the resolved OrderFill instead of being discarded.
    [Fact]
    public async Task ExecDetails_ContractLocalSymbolDiffersFromSubmittedSymbol_ResolvesOrderFillWithIbkrValue()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecDetailsTcsCallback(12004, requestedQuantity: 1);

        var contract = new Contract
        {
            Symbol = "SPX",
            SecType = "OPT",
            LocalSymbol = "SPXW260717C07500000"
        };
        var execution = new Execution
        {
            OrderId = 12004,
            AvgPrice = 3.10,
            CumQty = 1
        };

        wrapper.execDetails(0, contract, execution);

        Assert.True(tcs.Task.IsCompleted);
        var fill = await tcs.Task;
        Assert.Equal(3.10m, fill.AvgFillPrice);
        Assert.Equal("SPXW260717C07500000", fill.LocalSymbol);
    }

    // -- Partial-fill quantity gating (2026-07-17 UBER incident) --

    // Reproduces UBER's actual timeline: order for 5 filled 1 first, then the remaining 4
    // roughly nine minutes later. The TCS must not resolve on the first (partial) callback —
    // only the second, once cumulative filled reaches the full requested quantity.
    [Fact]
    public async Task ExecDetails_PartialThenFullSequence_ResolvesOnlyOnSecondCallback()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecDetailsTcsCallback(12184, requestedQuantity: 5);

        var contract = new Contract { Symbol = "UBER", SecType = "OPT", LocalSymbol = "UBER270617C00100000" };

        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 1 });

        Assert.False(tcs.Task.IsCompleted);

        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 5 });

        Assert.True(tcs.Task.IsCompleted);
        var fill = await tcs.Task;
        Assert.Equal(4.25m, fill.AvgFillPrice);
        Assert.Equal(5, fill.FilledQuantity);
    }

    // The inverse of the UBER incident: if the remainder genuinely never fills, the TCS must
    // never resolve as "Filled" — it must simply stay pending forever, so that whatever bounded
    // wait the caller applies (ClosePositionAsync/PartialCloseAsync/stop-order watch) is the
    // only thing that can end the wait, and it does so via the degraded-state path, not via a
    // false success here.
    [Fact]
    public async Task ExecDetails_NeverReachesFullQuantity_TcsNeverResolves()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecDetailsTcsCallback(12184, requestedQuantity: 5);

        var contract = new Contract { Symbol = "UBER", SecType = "OPT", LocalSymbol = "UBER270617C00100000" };

        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 1 });

        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
    }

    // A partial callback must invoke the caller's onPartialFill side-channel with the latest
    // confirmed fill data — this is what lets a caller (e.g. the stop-order watchdog) start a
    // bounded completion timer without treating the partial as done.
    [Fact]
    public void ExecDetails_PartialCallback_InvokesOnPartialFillWithLatestData()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        OrderFill? observed = null;
        wrapper.RegisterExecDetailsTcsCallback(12184, requestedQuantity: 5, onPartialFill: fill => observed = fill);

        var contract = new Contract { Symbol = "UBER", SecType = "OPT", LocalSymbol = "UBER270617C00100000" };
        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 1 });

        Assert.NotNull(observed);
        Assert.Equal(1, observed!.FilledQuantity);
        Assert.Equal(4.25m, observed.AvgFillPrice);
    }

    // The 2026-08-04 MPC incident: IBKR rejected an OCA target order with [110], an error code
    // with no dedicated branch in error(). Before this fix only 201/404/103 resolved a
    // registered rejection callback, so a [110] against a watched order id fell through to the
    // generic log-only branch and PlaceTrailWithTargetAsync's 600ms detection window expired
    // unresolved, reporting the leg as placed when IBKR had already rejected it.
    [Fact]
    public async Task Error_UnhandledCodeOnWatchedOrder_ResolvesRejectionCallback()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        wrapper.RegisterStopRejectionCallback(15384, tcs);

        wrapper.error(15384, 110, "The price does not conform to the minimum price variation for this contract.");

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        Assert.Contains("minimum price variation", await tcs.Task);
        Assert.Contains("minimum price variation", wrapper.TakeRejectionReason(15384));
    }

    [Fact]
    public void Error_UnhandledCodeOnUnwatchedOrder_DoesNotThrow()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);

        var ex = Record.Exception(() => wrapper.error(99999, 110, "Some unrelated rejection."));

        Assert.Null(ex);
    }

    // -- Execution history (reqExecutions) — GetRecentExecutionAsync's tier 1 --

    // GetRecentExecutionAsync queries account-level execution history by reqId, not orderId, a
    // ghost position's fill may not belong to any order this session is tracking. Confirms
    // execDetails accumulates into that separate reqId-keyed list and execDetailsEnd resolves
    // it, independent of the existing orderId-keyed order-fill-tracking dictionaries.
    [Fact]
    public async Task ExecutionHistory_AccumulatesUntilEnd_ResolvesWithAllMatchingExecutions()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecutionHistoryCallback(reqId: 500);

        var contract = new Contract { Symbol = "TSLA", SecType = "STK" };

        wrapper.execDetails(500, contract,
            new Execution { OrderId = 1, Side = "SLD", AvgPrice = 224.10, CumQty = 10, Time = "20260812  09:41:15" });
        wrapper.execDetails(500, contract,
            new Execution { OrderId = 2, Side = "SLD", AvgPrice = 225.00, CumQty = 10, Time = "20260812  09:52:03" });

        Assert.False(tcs.Task.IsCompleted);

        wrapper.execDetailsEnd(500);

        Assert.True(tcs.Task.IsCompleted);
        var executions = await tcs.Task;
        Assert.Equal(2, executions.Count);
        Assert.Contains(executions, e => e.Price == 225.00m);
    }

    // A malformed or unrecognized Time format must not drop the record silently or throw, it
    // still accumulates with a null Time so GetRecentExecutionAsync's own filtering excludes it
    // from ordering rather than the wrapper losing data.
    [Fact]
    public async Task ExecutionHistory_UnparsableTime_StillAccumulatesWithNullTime()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecutionHistoryCallback(reqId: 501);

        var contract = new Contract { Symbol = "TSLA", SecType = "STK" };
        wrapper.execDetails(501, contract,
            new Execution { OrderId = 3, Side = "SLD", AvgPrice = 226.00, CumQty = 10, Time = "garbage" });

        wrapper.execDetailsEnd(501);

        var executions = await tcs.Task;
        var only = Assert.Single(executions);
        Assert.Null(only.Time);
    }

    // -- Intraday bars — GetIntradayBarsAsync's tier 2 --

    // GetIntradayBarsAsync shares the same historicalData/historicalDataEnd callback stream as
    // GetHistoricalBarsAsync (daily) but resolves via a separate reqId-keyed registration that
    // parses the full timestamp instead of discarding time-of-day. Aug 12 2026 falls in Eastern
    // Daylight Time (UTC-4), so 09:41 ET is 13:41 UTC.
    [Fact]
    public async Task IntradayData_AccumulatesUntilEnd_ParsesFullTimestamp()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterIntradayDataCallback(reqId: 700);

        wrapper.historicalData(700, new Bar("20260812  09:41:00", 221.0, 223.0, 220.5, 222.25, 1000, 1, 222.0));

        Assert.False(tcs.Task.IsCompleted);

        wrapper.historicalDataEnd(700, "", "");

        var bars = await tcs.Task;
        var bar = Assert.Single(bars);
        Assert.Equal(222.25m, bar.Close);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 13, 41, 0, TimeSpan.Zero), bar.Time);
    }
}
