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

    // Placeholder — implemented in feat/ibkr-place-order
    public Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken ct = default) =>
        throw new NotImplementedException(
            "PlaceOrderAsync not yet implemented — use NullBrokerService for testing");

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
}