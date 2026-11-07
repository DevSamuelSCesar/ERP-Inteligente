using erp_inteligente.Core.Audit;

namespace Nexus.Tests.Core.Audit;

public class AuditEntryTests
{
    private static AuditEntry Entry(string action = "stock.move", string? target = "sku:ABC-123", string? details = null) =>
        new(AuditActorType.User, 1, null, action, target, AuditOutcome.Allowed, details);

    [Theory]
    [InlineData("stock.move")]
    [InlineData("a")]
    [InlineData("plan_submit.v2")]
    public void ValidAction_IsAccepted(string action)
    {
        Assert.Equal(action, Entry(action: action).Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Stock.move")]
    [InlineData("1stock")]
    [InlineData("stock move")]
    [InlineData("stock.move\n")]
    [InlineData("stock-move")]
    public void InvalidAction_IsRejected(string action)
    {
        Assert.Throws<ArgumentException>(() => Entry(action: action));
    }

    [Fact]
    public void ActionOf100Characters_IsAcceptedAnd101_IsRejected()
    {
        var max = "a" + new string('b', 99);
        Assert.Equal(100, Entry(action: max).Action.Length);
        Assert.Throws<ArgumentException>(() => Entry(action: max + "b"));
    }

    [Fact]
    public void TargetOf200Characters_IsAccepted_AndLongerIsRejected()
    {
        var max = new string('t', AuditEntry.MaxTargetLength);
        Assert.Equal(max, Entry(target: max).Target);
        Assert.Throws<ArgumentException>(() => Entry(target: max + "t"));
    }

    [Fact]
    public void NullTarget_IsAccepted()
    {
        Assert.Null(Entry(target: null).Target);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{\"a\":1,}")]
    [InlineData("not json")]
    public void InvalidDetailsJson_IsRejected(string details)
    {
        Assert.Throws<ArgumentException>(() => Entry(details: details));
    }

    [Fact]
    public void DetailsJson_OfExactly4000Characters_IsAccepted_AndLongerIsRejected()
    {
        // A JSON string literal of 4000 characters: two quotes plus 3998 letters.
        var max = "\"" + new string('a', AuditEntry.MaxDetailsJsonLength - 2) + "\"";
        Assert.Equal(AuditEntry.MaxDetailsJsonLength, Entry(details: max).DetailsJson!.Length);
        Assert.Throws<ArgumentException>(() => Entry(details: "\"" + new string('a', AuditEntry.MaxDetailsJsonLength - 1) + "\""));
    }

    [Fact]
    public void WithExpression_CannotBypassValidation()
    {
        var copy = Entry() with { Action = "Bad Action" };
        Assert.Throws<ArgumentException>(() => copy.EnsureValid());
    }

    [Fact]
    public void ActorTypes_MapToLowercaseDatabaseText()
    {
        Assert.Equal("user", AuditActorType.User.ToDbValue());
        Assert.Equal("ai", AuditActorType.Ai.ToDbValue());
        Assert.Equal("system", AuditActorType.System.ToDbValue());
        Assert.Equal(AuditActorType.Ai, AuditActorTypes.FromDbValue("ai"));
        Assert.Throws<ArgumentException>(() => AuditActorTypes.FromDbValue("AI"));
    }

    [Fact]
    public void Outcomes_MapToLowercaseDatabaseText()
    {
        Assert.Equal("allowed", AuditOutcome.Allowed.ToDbValue());
        Assert.Equal("denied", AuditOutcome.Denied.ToDbValue());
        Assert.Equal("failed", AuditOutcome.Failed.ToDbValue());
        Assert.Equal(AuditOutcome.Denied, AuditOutcomes.FromDbValue("denied"));
        Assert.Throws<ArgumentException>(() => AuditOutcomes.FromDbValue("ok"));
    }
}
