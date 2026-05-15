using TradeFlow.AlertPoC.Models;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Engine;

// Converts an approved Alert into a TradeOrder with sizing, stop, and target prices.
// Fixed sizing rules for paper trading phase:
//   Options: $1,000 initial, $500 average
//   Stocks:  $3,000 initial, $1,500 average
public class PositionSizer
{
    // Options sizing
    private const decimal OptionsInitialBudget  = 1_000m;
    private const decimal OptionsAverageBudget  =   500m;
    private const decimal OptionsStopMultiplier =  0.50m; // -50%
    private const decimal OptionsTgtMultiplier  =  3.00m; // +200%

    // Stock sizing
    private const decimal StockInitialBudget    = 3_000m;
    private const decimal StockAverageBudget    = 1_500m;
    private const decimal StockStopMultiplier   =  0.85m; // -15%
    private const decimal StockTgtMultiplier    =  1.30m; // +30%

    // Minimum quantity, never place a zero-contract/share order
    private const int MinQuantity = 1;

    public TradeOrder? Size(Alert alert, AlertClassification classification, bool isAverage = false)
    {
        var price = alert.PricePaid;
        if (price is null or <= 0)
            return null;

        var tradeType = classification.Category switch
        {
            AlertCategory.CallOptionEntry or
            AlertCategory.PutOptionEntry  => TradeType.Options,
            AlertCategory.StockEntry      => TradeType.Stock,
            _                             => (TradeType?)null
        };

        if (tradeType is null)
            return null;

        var isOptions = tradeType == TradeType.Options;

        var budget = isOptions
            ? (isAverage ? OptionsAverageBudget : OptionsInitialBudget)
            : (isAverage ? StockAverageBudget   : StockInitialBudget);

        // Options contracts represent 100 shares (divide by price × 100)
        var quantity = isOptions
            ? (int)(budget / (price.Value * 100))
            : (int)(budget / price.Value);

        if (quantity < MinQuantity)
            return null;

        var stopPrice = isOptions
            ? price.Value * OptionsStopMultiplier
            : price.Value * StockStopMultiplier;

        var targetPrice = isOptions
            ? price.Value * OptionsTgtMultiplier
            : price.Value * StockTgtMultiplier;

        // Actual budget used may vary slightly
        var budgetUsed = isOptions
            ? quantity * price.Value * 100
            : quantity * price.Value;

        return new TradeOrder(
            AlertId:               alert.Id ?? string.Empty,
            UserName:              alert.UserName ?? string.Empty,
            Symbol:                alert.Symbol   ?? string.Empty,
            TradeType:             tradeType.Value,
            OptionsContractSymbol: alert.OptionsContractSymbol,
            Direction:             alert.Direction,
            Strike:                alert.Strike,
            Expiration:            alert.Expiration,
            Quantity:              quantity,
            EstimatedEntryPrice:   price.Value,
            BudgetUsed:            budgetUsed,
            StopPrice:             stopPrice,
            TargetPrice:           targetPrice,
            IsAverage:             isAverage);
    }
}