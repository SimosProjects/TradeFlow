using System.ComponentModel.DataAnnotations;

namespace TradeFlow.Worker.Configuration;

public class RiskEngineOptions
{
    public const string SectionName = "RiskEngine";

    [Range(0, 100, ErrorMessage = "MinXScore must be between 0 and 100.")]
    public int MinXScore { get; init; } = 60;

    [MinLength(1, ErrorMessage = "At least one approved trader must be specified.")]
    public List<string> ApprovedTraders { get; init; } = [];

    public bool AllowLotto { get; init; } = false;
}