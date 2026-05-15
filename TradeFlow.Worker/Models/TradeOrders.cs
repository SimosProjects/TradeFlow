namespace TradeFlow.Worker.Models;

/// <summary>
/// Represents an order to be placed with the broker.
/// Built by PositionSizer from an approved Alert.
/// </summary>
public record TradeOrder(
    // Identity
    string   AlertId,
    string   UserName,

    // Instrument
    string   Symbol,
    TradeType TradeType,            // Options or Stock
    string?  OptionsContractSymbol, // OCC symbol e.g. TSLA260620C00450000
    string?  Direction,             // call, put, none
    decimal? Strike,
    string?  Expiration,

    // Order details
    int      Quantity,              // contracts (options) or shares (stocks)
    decimal  EstimatedEntryPrice,   // from alert pricePaid
    decimal  BudgetUsed,            // actual dollar amount committed

    // Risk management
    decimal  StopPrice,             // calculated stop loss price
    decimal  TargetPrice,           // calculated profit target price

    // Metadata
    bool     IsAverage = false      // true if this is an averaging order
);