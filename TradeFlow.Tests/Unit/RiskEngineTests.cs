namespace TradeFlow.Tests.Unit;

public class RiskEngineTests
{
    // Helper — builds a minimal valid alert
    private static Alert BuildAlert(
        string side = "bto",
        string risk = "standard",
        double xScore = 75.0,
        string userName = "yoyomun") =>
        new(Id: "test-id", UserId: null, UserName: userName,
            Symbol: "TSLA", Type: "options", Direction: "call",
            Strike: 395m, Expiration: null,
            OptionsContractSymbol: null, ContractDescription: null,
            Side: side, Status: null, Result: null,
            ActualPriceAtTimeOfAlert: null, ActualPriceAtTimeOfExit: null,
            PricePaid: null, PriceAtExit: null,
            HighestPrice: null, LowestPrice: null,
            LastCheckedPrice: null, Risk: risk,
            LastKnownPercentProfit: null, IsProfitableTrade: null,
            XScore: xScore, CanAverage: null,
            TimeOfEntryAlert: null, TimeOfFullExitAlert: null,
            FormattedLength: null, IsSwing: null,
            IsBullish: null, IsShort: null,
            Strategy: null, OriginalMessage: null,
            OriginalExitMessage: null);

    // -- EntryOnlyRule --
    [Fact]
    public void EntryOnlyRule_BtoSide_Passes()
    {
        var rule   = new EntryOnlyRule();
        var alert  = BuildAlert(side: "bto");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("stc")]
    [InlineData("btc")]
    [InlineData("sto")]
    public void EntryOnlyRule_NonBtoSide_Fails(string side)
    {
        var rule   = new EntryOnlyRule();
        var alert  = BuildAlert(side: side);
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    // -- NoLottoRule --
    [Fact]
    public void NoLottoRule_StandardRisk_Passes()
    {
        var rule   = new NoLottoRule();
        var alert  = BuildAlert(risk: "standard");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void NoLottoRule_LottoRisk_Fails()
    {
        var rule   = new NoLottoRule();
        var alert  = BuildAlert(risk: "lotto");
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
        Assert.Contains("lotto", result.Reason);
    }

    // -- MinXScoreRule --
    [Theory]
    [InlineData(60.0, 60.0, true)]   // exactly at threshold: passes
    [InlineData(60.0, 59.9, false)]  // just below: fails
    [InlineData(60.0, 100.0, true)]  // well above: passes
    [InlineData(60.0, 0.0, false)]   // zero: fails
    public void MinXScoreRule_Threshold_CorrectResult(
        double threshold, double xScore, bool expectedPassed)
    {
        var rule   = new MinXScoreRule(threshold);
        var alert  = BuildAlert(xScore: xScore);
        var result = rule.Evaluate(alert);
        Assert.Equal(expectedPassed, result.Passed);
    }

    [Fact]
    public void MinXScoreRule_NullXScore_Fails()
    {
        // Null XScore should be treated as 0, below any threshold
        var rule  = new MinXScoreRule(60.0);
        var alert = BuildAlert() with { XScore = null };
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    // -- ApprovedTraderRule --
    [Fact]
    public void ApprovedTraderRule_ApprovedTrader_Passes()
    {
        var rule   = new ApprovedTraderRule(["yoyomun", "Fibonaccizer"]);
        var alert  = BuildAlert(userName: "yoyomun");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ApprovedTraderRule_CaseInsensitive_Passes()
    {
        // Trader name matching should be case-insensitive
        var rule   = new ApprovedTraderRule(["yoyomun"]);
        var alert  = BuildAlert(userName: "YOYOMUN");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ApprovedTraderRule_UnknownTrader_Fails()
    {
        var rule   = new ApprovedTraderRule(["yoyomun"]);
        var alert  = BuildAlert(userName: "unknown_trader");
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    // -- Short-circuit behavior in Risk Engine Service --
    [Fact]
    public void RiskEngine_FirstRuleFails_ShortCircuits()
    {
        // Arrange, mock a rule that always fails
        var failingRule = new Mock<IRiskRule>();
        failingRule
            .Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Fail("First rule failed"));

        var neverCalledRule = new Mock<IRiskRule>();

        var engine = new RiskEngineService([
            failingRule.Object,
            neverCalledRule.Object
        ]);

        var alert = BuildAlert();

        // Act
        var result = engine.Evaluate(alert);

        // Assert
        Assert.False(result.Approved);
        Assert.Equal("First rule failed", result.Reason);

        // The second rule should never have been called
        neverCalledRule.Verify(
            r => r.Evaluate(It.IsAny<Alert>()),
            Times.Never);
    }

    [Fact]
    public void RiskEngine_AllRulesPass_ReturnsApproved()
    {
        // Arrange, mock rules that always pass
        var passingRule1 = new Mock<IRiskRule>();
        passingRule1
            .Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Pass("Rule 1 passed"));

        var passingRule2 = new Mock<IRiskRule>();
        passingRule2
            .Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Pass("Rule 2 passed"));

        var engine = new RiskEngineService([
            passingRule1.Object,
            passingRule2.Object
        ]);

        var alert = BuildAlert();

        // Act
        var result = engine.Evaluate(alert);

        // Assert
        Assert.True(result.Approved);

        // Both rules should have been called
        passingRule1.Verify(r => r.Evaluate(It.IsAny<Alert>()), Times.Once);
        passingRule2.Verify(r => r.Evaluate(It.IsAny<Alert>()), Times.Once);
    }
}