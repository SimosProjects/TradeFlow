using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Abstraction over a broker API. Active implementation switches from
/// <see cref="NullBrokerService"/> during testing to <see cref="IbkrBrokerService"/>
/// for paper and live trading.
/// </summary>
public interface IBrokerService
{
    /// <summary>
    /// Places a bracket order consisting of a market entry, stop loss, and profit target.
    /// </summary>
    /// <param name="order">The trade order built by PositionSizer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="BrokerOrderResult"/> with fill details.</returns>
    Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current market price of an open position from IBKR position data.
    /// Used by PositionMonitorService to evaluate stop and target thresholds.
    /// </summary>
    /// <param name="trade">The open trade record to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current price per share or contract, or 0 if not found.</returns>
    Task<decimal> GetCurrentPositionPriceAsync(
        TradeRecord trade,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels pending stop and target orders then places a market close order.
    /// </summary>
    /// <param name="trade">The open trade record to close.</param>
    /// <param name="outcome">The reason for closing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="BrokerOrderResult"/> with close fill details.</returns>
    Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the net liquidation value of the account.
    /// Used by TradeGuard for exposure checks before placing orders.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Account balance in USD.</returns>
    Task<decimal> GetAccountBalanceAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total market value of all open positions.
    /// Used by TradeGuard to calculate current exposure before new orders.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Gross position value in USD.</returns>
    Task<decimal> GetOpenPositionsValueAsync(
        CancellationToken cancellationToken = default);
}