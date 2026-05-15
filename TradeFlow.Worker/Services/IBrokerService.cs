using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

// Abstraction over a broker API.
// Active implementation: NullBrokerService (testing) → IbkrBrokerService (IBKR paper/live)
public interface IBrokerService
{
    // Places a bracket order: entry + stop loss + profit target
    Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default);

    // Cancels pending stop/target orders and places a market close order
    Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken cancellationToken = default);

    // Net liquidation value — used by TradeGuard for exposure checks
    Task<decimal> GetAccountBalanceAsync(
        CancellationToken cancellationToken = default);

    // Total current market value of all open positions
    Task<decimal> GetOpenPositionsValueAsync(
        CancellationToken cancellationToken = default);
}