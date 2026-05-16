using IBApi;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// IBKR broker implementation using the TWS API via IB Gateway.
/// Requires IB Gateway running on localhost:4002 (paper) or 4001 (live trading).
/// Swap <see cref="NullBrokerService"/> for this in Program.cs when ready for paper trading.
/// </summary>
public class IbkrBrokerService : IBrokerService
{
    private readonly IbkrConnectionService _connection;
    private readonly IbkrOptions _options;
    private readonly ILogger<IbkrBrokerService> _logger;

    private int _nextReqId = 1;

    public IbkrBrokerService(
        IbkrConnectionService connection,
        IOptions<IbkrOptions> options,
        ILogger<IbkrBrokerService> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns the net liquidation value of the IBKR account.
    /// Used by <see cref="TradeGuard"/> to verify available capital before placing orders.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account balance in USD, or 0 if the request fails or times out.</returns>
    public async Task<decimal> GetAccountBalanceAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return 0m;

        var reqId = NextReqId();
        var tcs = _connection.Wrapper.RegisterAccountCallback(reqId);

        _connection.Client.reqAccountSummary(reqId, "All", "NetLiquidation");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var valueStr = await tcs.Task.WaitAsync(cts.Token);

            if (decimal.TryParse(valueStr, out var balance))
            {
                _logger.LogInformation("IBKR account balance: ${Balance:F2}", balance);
                return balance;
            }

            _logger.LogWarning(
                "IBKR GetAccountBalance could not parse value: {Value}", valueStr);
            return 0m;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetAccountBalance timed out");
            return 0m;
        }
        finally
        {
            _connection.Client.cancelAccountSummary(reqId);
        }
    }

    /// <summary>
    /// Returns the total market value of all open positions.
    /// Used by <see cref="TradeGuard"/> to calculate current exposure before new orders.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Gross position value in USD, or 0 if the request fails or times out.</returns>
    public async Task<decimal> GetOpenPositionsValueAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return 0m;

        var reqId = NextReqId();
        var tcs = _connection.Wrapper.RegisterAccountCallback(reqId);

        _connection.Client.reqAccountSummary(reqId, "All", "GrossPositionValue");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var valueStr = await tcs.Task.WaitAsync(cts.Token);

            if (decimal.TryParse(valueStr, out var value))
            {
                _logger.LogInformation("IBKR open positions value: ${Value:F2}", value);
                return value;
            }

            return 0m;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetOpenPositionsValue timed out");
            return 0m;
        }
        finally
        {
            _connection.Client.cancelAccountSummary(reqId);
        }
    }

    /// <summary>
    /// Places a bracket order with IBKR consisting of a market entry, stop loss, and profit target.
    /// All three orders transmit atomically by setting Transmit=false on the parent and stop,
    /// then Transmit=true on the target which triggers submission of the full bracket.
    /// </summary>
    /// <param name="order">The trade order built by <see cref="PositionSizer"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BrokerOrderResult"/> with fill details. Returns <see cref="OrderStatus.Pending"/>
    /// if confirmation times out as the order may still be working at IBKR.
    /// </returns>
    public async Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        var orderId = GetNextOrderId();
        var tcs = _connection.Wrapper.RegisterOrderCallback(orderId);
        var contract = BuildContract(order);
        var parentOrder = BuildMarketOrder(orderId, order.Quantity, "BUY");
        var stopOrder = BuildStopOrder(orderId + 1, orderId, order.Quantity, (double)order.StopPrice);
        var targetOrder = BuildLimitOrder(orderId + 2, orderId, order.Quantity, (double)order.TargetPrice);

        // Transmit=false on parent and stop so all three submit together when target is placed
        parentOrder.Transmit = false;
        stopOrder.Transmit = false;
        targetOrder.Transmit = true;

        try
        {
            _connection.Client.placeOrder(orderId, contract, parentOrder);
            _connection.Client.placeOrder(orderId + 1, contract, stopOrder);
            _connection.Client.placeOrder(orderId + 2, contract, targetOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var state = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogInformation(
                "IBKR order placed. OrderId: {OrderId} Status: {Status}",
                orderId, state.Status);

            return new BrokerOrderResult(
                OrderId: orderId.ToString(),
                StopOrderId: (orderId + 1).ToString(),
                TargetOrderId: (orderId + 2).ToString(),
                FillPrice: order.EstimatedEntryPrice,
                FillQuantity: order.Quantity,
                FillAmount: order.BudgetUsed,
                Status: OrderStatus.Filled,
                FilledAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR PlaceOrder timed out for {Symbol}. Order may still be pending.",
                order.Symbol);

            return new BrokerOrderResult(
                OrderId: orderId.ToString(),
                StopOrderId: (orderId + 1).ToString(),
                TargetOrderId: (orderId + 2).ToString(),
                FillPrice: order.EstimatedEntryPrice,
                FillQuantity: order.Quantity,
                FillAmount: order.BudgetUsed,
                Status: OrderStatus.Pending,
                FilledAt: DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Cancels any active stop and target orders then places a market close order.
    /// </summary>
    /// <param name="trade">The open <see cref="TradeRecord"/> to close.</param>
    /// <param name="outcome">The reason for closing, stored on the trade record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BrokerOrderResult"/> with close fill details. Fill price is estimated
    /// from <see cref="TradeRecord.ExitPrice"/> if available, otherwise falls back to entry price.
    /// </returns>
    public async Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        if (trade.StopOrderId is not null &&
            int.TryParse(trade.StopOrderId, out var stopId))
        {
            _connection.Client.cancelOrder(stopId);
            _logger.LogInformation(
                "IBKR cancelled stop order {OrderId} for {Symbol}", stopId, trade.Symbol);
        }

        if (trade.TargetOrderId is not null &&
            int.TryParse(trade.TargetOrderId, out var targetId))
        {
            _connection.Client.cancelOrder(targetId);
            _logger.LogInformation(
                "IBKR cancelled target order {OrderId} for {Symbol}", targetId, trade.Symbol);
        }

        // Give IBKR time to process the cancellations before placing the close order
        await Task.Delay(500, ct);

        var closeOrderId = GetNextOrderId();
        var tcs = _connection.Wrapper.RegisterOrderCallback(closeOrderId);
        var contract = BuildCloseContract(trade);
        var closeOrder = BuildCloseOrder(closeOrderId, trade);

        try
        {
            _connection.Client.placeOrder(closeOrderId, contract, closeOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var state = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogInformation(
                "IBKR position closed. OrderId: {OrderId} Symbol: {Symbol} Status: {Status}",
                closeOrderId, trade.Symbol, state.Status);

            var estimatedFill = trade.ExitPrice ?? trade.EntryPrice;
            var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;

            return new BrokerOrderResult(
                OrderId: closeOrderId.ToString(),
                StopOrderId: null,
                TargetOrderId: null,
                FillPrice: estimatedFill,
                FillQuantity: trade.Quantity,
                FillAmount: estimatedFill * trade.Quantity * multiplier,
                Status: OrderStatus.Filled,
                FilledAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR ClosePosition timed out for {Symbol}. Close order may still be pending.",
                trade.Symbol);

            return new BrokerOrderResult(
                OrderId: closeOrderId.ToString(),
                StopOrderId: null,
                TargetOrderId: null,
                FillPrice: trade.EntryPrice,
                FillQuantity: trade.Quantity,
                FillAmount: trade.EntryAmount,
                Status: OrderStatus.Pending,
                FilledAt: DateTimeOffset.UtcNow);
        }
    }

    // Connects to IB Gateway if not already connected
    private bool EnsureConnected()
    {
        if (_connection.IsConnected)
            return true;

        var connected = _connection.Connect();
        if (!connected)
            _logger.LogError(
                "Cannot reach IB Gateway. Is it running on {Host}:{Port}?",
                _options.Host, _options.Port);

        return connected;
    }

    private int NextReqId() => Interlocked.Increment(ref _nextReqId);

    // Leaves gaps of 10 between parent IDs to accommodate stop and target child order IDs
    private int GetNextOrderId() => Interlocked.Add(ref _nextReqId, 10);

    private static Contract BuildContract(TradeOrder order)
    {
        if (order.TradeType == TradeType.Options)
        {
            return new Contract
            {
                Symbol = order.Symbol,
                SecType = "OPT",
                Exchange = "SMART",
                Currency = "USD",
                Right = order.Direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike = (double)(order.Strike ?? 0),
                LastTradeDateOrContractMonth =
                    order.Expiration is not null
                        ? DateTimeOffset.Parse(order.Expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier = "100",
            };
        }

        return new Contract
        {
            Symbol = order.Symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD",
        };
    }

    private static Contract BuildCloseContract(TradeRecord trade)
    {
        if (trade.TradeType == TradeType.Options)
        {
            return new Contract
            {
                Symbol = trade.Symbol,
                SecType = "OPT",
                Exchange = "SMART",
                Currency = "USD",
                Right = trade.Direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike = (double)(trade.Strike ?? 0),
                LastTradeDateOrContractMonth =
                    trade.Expiration is not null
                        ? DateTimeOffset.Parse(trade.Expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier = "100",
            };
        }

        return new Contract
        {
            Symbol = trade.Symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD",
        };
    }

    private static Order BuildMarketOrder(int orderId, int quantity, string action) =>
        new()
        {
            OrderId = orderId,
            Action = action,
            OrderType = "MKT",
            TotalQuantity = quantity,
            Transmit = false,
        };

    private static Order BuildStopOrder(int orderId, int parentId, int quantity, double stopPrice) =>
        new()
        {
            OrderId = orderId,
            ParentId = parentId,
            Action = "SELL",
            OrderType = "STP",
            AuxPrice = stopPrice,
            TotalQuantity = quantity,
            Transmit = false,
        };

    private static Order BuildLimitOrder(int orderId, int parentId, int quantity, double limitPrice) =>
        new()
        {
            OrderId = orderId,
            ParentId = parentId,
            Action = "SELL",
            OrderType = "LMT",
            LmtPrice = limitPrice,
            TotalQuantity = quantity,
            Transmit = false,
        };

    private static Order BuildCloseOrder(int orderId, TradeRecord trade) =>
        new()
        {
            OrderId = orderId,
            Action = "SELL",
            OrderType = "MKT",
            TotalQuantity = trade.Quantity,
            Transmit = true,
        };

    private static BrokerOrderResult FailedResult(string reason) =>
        new(
            OrderId: "FAILED",
            StopOrderId: null,
            TargetOrderId: null,
            FillPrice: 0m,
            FillQuantity: 0,
            FillAmount: 0m,
            Status: OrderStatus.Rejected,
            FilledAt: DateTimeOffset.UtcNow);
}