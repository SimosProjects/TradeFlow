using System.ComponentModel.DataAnnotations;

namespace TradeFlow.Worker.Configuration;

public class PollingOptions
{
    public const string SectionName = "Polling";

    [Range(5, 300, ErrorMessage = "IntervalSeconds must be between 5 and 300.")]
    public int IntervalSeconds { get; init; } = 30;
}