namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Rejects options alerts when options trading is disabled in configuration.
/// Stock alerts are not affected by this rule.
/// </summary>
public class AllowOptionsRule : IRiskRule
{
    private readonly bool _allowOptions;

    public AllowOptionsRule(bool allowOptions)
    {
        _allowOptions = allowOptions;
    }

    public RuleResult Evaluate(Alert alert)
    {
        if (_allowOptions)
            return RuleResult.Pass("Options trading is enabled");

        var isOptions = alert.Type?.ToLowerInvariant() == "options";

        return isOptions
            ? RuleResult.Fail("Rejected - options trading is disabled")
            : RuleResult.Pass("Not an options alert");
    }
}
