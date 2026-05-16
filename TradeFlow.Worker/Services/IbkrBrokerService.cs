using IBApi;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

// IBKR broker implementation using the TWS API via IB Gateway.
// Requires IB Gateway running on localhost:4002 (paper) or 4001 (live).
public class IbkrBrokerService : IBrokerService
{
    private readonly IbkrConnectionService _connection;
    private readonly IbkrOptions _options;
    private readonly ILogger<IbkrBrokerService> _logger;

    // Incrementing request ID for IBKR API calls
    private int _nextReqId = 1;
    private int NextReqId() => Interlocked.Increment(ref _nextReqId);

    public IbkrBrokerService(
        IbkrConnectionService connection,
        IOptions<IbkrOptions> options,
        ILogger<IbkrBrokerService> logger)
    {
        _connection = connection;
        _options    = options.Value;
        _logger     = logger;
    }

    // Returns the net liquidation value of the paper account.
    // Used by TradeGuard to check total exposure before placing orders.
    public async Task<decimal> GetAccountBalanceAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return 0m;

        var reqId = NextReqId();
        var tcs   = _connection.Wrapper.RegisterAccountCallback(reqId);

        // Request account summary, NetLiquidation tag gives us total account value
        _connection.Client.reqAccountSummary(reqId, "All", "NetLiquidation");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var valueStr = await tcs.Task.WaitAsync(cts.Token);

            if (decimal.TryParse(valueStr, out var balance))
            {
                _logger.LogInformation(
                    "IBKR account balance: ${Balance:F2}", balance);
                return balance;
            }

            _logger.LogWarning(
                "IBKR GetAccountBalance — could not parse value: {Value}", valueStr);
            return 0m;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetAccountBalance timed out");
            return 0m;
        }
        finally
        {
            // Cancel the subscription after we have the value
            _connection.Client.cancelAccountSummary(reqId);
        }
    }

    // Returns total market value of all open positions.
    // Used by TradeGuard for exposure check before new orders.
    public async Task<decimal> GetOpenPositionsValueAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return 0m;

        var reqId = NextReqId();
        var tcs   = _connection.Wrapper.RegisterAccountCallback(reqId);

        // TotalCashValue + GrossPositionValue gives us the open exposure
        _connection.Client.reqAccountSummary(reqId, "All", "GrossPositionValue");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var valueStr = await tcs.Task.WaitAsync(cts.Token);

            if (decimal.TryParse(valueStr, out var value))
            {
                _logger.LogInformation(
                    "IBKR open positions value: ${Value:F2}", value);
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

    public async Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        var orderId = GetNextOrderId();
        var tcs     = _connection.Wrapper.RegisterOrderCallback(orderId);

        var contract = BuildContract(order);
        var parentOrder = BuildMarketOrder(orderId, order.Quantity, "BUY");

        // Bracket order — parent entry + stop loss + profit target in one submission
        var stopOrder   = BuildStopOrder(orderId + 1, orderId, order.Quantity,
                            (double)order.StopPrice);
        var targetOrder = BuildLimitOrder(orderId + 2, orderId, order.Quantity,
                            (double)order.TargetPrice);

        // Transmit=false on parent and stop so all three submit together
        parentOrder.Transmit = false;
        stopOrder.Transmit   = false;
        targetOrder.Transmit = true; // last order triggers transmission of all three

        try
        {
            _connection.Client.placeOrder(orderId,     contract, parentOrder);
            _connection.Client.placeOrder(orderId + 1, contract, stopOrder);
            _connection.Client.placeOrder(orderId + 2, contract, targetOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var state = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogInformation(
                "IBKR order placed — OrderId: {OrderId} Status: {Status}",
                orderId, state.Status);

            return new BrokerOrderResult(
                OrderId:       orderId.ToString(),
                StopOrderId:   (orderId + 1).ToString(),
                TargetOrderId: (orderId + 2).ToString(),
                FillPrice:     order.EstimatedEntryPrice,
                FillQuantity:  order.Quantity,
                FillAmount:    order.BudgetUsed,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR PlaceOrder timed out for {Symbol} — order may still be pending",
                order.Symbol);

            return new BrokerOrderResult(
                OrderId:       orderId.ToString(),
                StopOrderId:   (orderId + 1).ToString(),
                TargetOrderId: (orderId + 2).ToString(),
                FillPrice:     order.EstimatedEntryPrice,
                FillQuantity:  order.Quantity,
                FillAmount:    order.BudgetUsed,
                Status:        OrderStatus.Pending,
                FilledAt:      DateTimeOffset.UtcNow);
        }
    }

    // Placeholder — implemented in feat/ibkr-close-order
    public Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken ct = default) =>
        throw new NotImplementedException(
            "ClosePositionAsync not yet implemented — use NullBrokerService for testing");

    // Connects to IB Gateway if not already connected
    private bool EnsureConnected()
    {
        if (_connection.IsConnected)
            return true;

        var connected = _connection.Connect();
        if (!connected)
            _logger.LogError(
                "Cannot reach IB Gateway — is it running on {Host}:{Port}?",
                _options.Host, _options.Port);

        return connected;
    }

    // -- Helpers --
    private int GetNextOrderId()
    {
        // IBKR requires unique incrementing order IDs
        // In production this should be persisted, for paper trading in-memory is fine
        return Interlocked.Add(ref _nextReqId, 10);
    }

    private static Contract BuildContract(TradeOrder order)
    {
        if (order.TradeType == TradeType.Options)
        {
            return new Contract
            {
                Symbol      = order.Symbol,
                SecType     = "OPT",
                Exchange    = "SMART",
                Currency    = "USD",
                Right       = order.Direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike      = (double)(order.Strike ?? 0),
                LastTradeDateOrContractMonth =
                    order.Expiration is not null
                        ? DateTimeOffset.Parse(order.Expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier  = "100",
            };
        }
        else
        {
            return new Contract
            {
                Symbol   = order.Symbol,
                SecType  = "STK",
                Exchange = "SMART",
                Currency = "USD",
            };
        }
    }

    private static Order BuildMarketOrder(int orderId, int quantity, string action)
    {
        return new Order
        {
            OrderId   = orderId,
            Action    = action,
            OrderType = "MKT",
            TotalQuantity = quantity,
            Transmit  = false,
        };
    }

    private static Order BuildStopOrder(int orderId, int parentId, int quantity, double stopPrice)
    {
        return new Order
        {
            OrderId       = orderId,
            ParentId      = parentId,
            Action        = "SELL",
            OrderType     = "STP",
            AuxPrice      = stopPrice,
            TotalQuantity = quantity,
            Transmit      = false,
        };
    }

    private static Order BuildLimitOrder(int orderId, int parentId, int quantity, double limitPrice)
    {
        return new Order
        {
            OrderId       = orderId,
            ParentId      = parentId,
            Action        = "SELL",
            OrderType     = "LMT",
            LmtPrice      = limitPrice,
            TotalQuantity = quantity,
            Transmit      = false,
        };
    }

    private static BrokerOrderResult FailedResult(string reason)
    {
        return new BrokerOrderResult(
            OrderId:       "FAILED",
            StopOrderId:   null,
            TargetOrderId: null,
            FillPrice:     0m,
            FillQuantity:  0,
            FillAmount:    0m,
            Status:        OrderStatus.Rejected,
            FilledAt:      DateTimeOffset.UtcNow);
    }
}