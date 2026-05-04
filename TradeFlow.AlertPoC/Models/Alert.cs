namespace TradeFlow.AlertPoC.Models;

// Flexible response wrapper — Xtrades hasn't published a formal API contract,
// so we cover the three most common root-level list field names seen in practice.
// Once the real shape is confirmed, the unused properties can be removed.
public record AlertsResponse(
    [property: JsonPropertyName("alerts")] List<Alert>? Alerts,
    [property: JsonPropertyName("data")]   List<Alert>? Data,
    [property: JsonPropertyName("items")]  List<Alert>? Items
);

// Immutable DTO representing a single Xtrades alert off the wire.
// Modelled as a record so value-based equality works for deduplication
// without needing to override Equals/GetHashCode manually.
// Nullable properties reflect the API reality — commons alerts carry no
// strike, expiry, or contract symbol.
public record Alert(
    [property: JsonPropertyName("id")]                       string?  Id,
    [property: JsonPropertyName("userName")]                 string?  UserName,
    [property: JsonPropertyName("userId")]                   string?  UserId,
    [property: JsonPropertyName("symbol")]                   string?  Symbol,
    [property: JsonPropertyName("side")]                     string?  Side,

    // "commons" or "options" — drives downstream filtering logic
    [property: JsonPropertyName("type")]                     string?  Type,

    // "call", "put", or "none" for commons
    [property: JsonPropertyName("direction")]                string?  Direction,

    [property: JsonPropertyName("strike")]                   decimal? Strike,
    [property: JsonPropertyName("expiration")]               string?  Expiration,
    [property: JsonPropertyName("optionsContractSymbol")]    string?  OptionsContractSymbol,
    [property: JsonPropertyName("actualPriceAtTimeOfAlert")] decimal? ActualPriceAtTimeOfAlert,
    [property: JsonPropertyName("timeOfEntryAlert")]         string?  TimeOfEntryAlert,
    [property: JsonPropertyName("status")]                   string?  Status,
    [property: JsonPropertyName("originalMessage")]          string?  OriginalMessage
);