namespace TradeFlow.AlertPoC.RiskEngine;


/// <summary>
/// Rejects any alert with a 'Risk' property of "lotto", which indicates a very high-risk alert that does not fit our execution strategy.
/// Standard and high risk levels are permitted.
/// </summary>
public class NoLottoRule : IRiskRule
{
    public RuleResult Evaluate(Alert alert) =>
        alert.Risk != "lotto"
            ? RuleResult.Pass($"Risk level acceptable")
            : RuleResult.Fail("Rejected - lotto risk alerts are excluded");
}