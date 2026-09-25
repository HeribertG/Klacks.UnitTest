// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The scope of one chat turn remembers which confirmation tokens and proposal hints the turn issued, so a
/// stopped turn can drop them. The older sensitive-token memory keeps its own meaning: only a token of a
/// sensitive skill is refused for same-turn redemption.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;

namespace Klacks.UnitTest.Application.Services.Assistant.Autonomy;

[TestFixture]
public class TurnConfirmationScopeTests
{
    private const string GateToken = "gate-token";
    private const string SensitiveToken = "sensitive-token";
    private const string ApplySkill = "apply_proposal";

    private TurnConfirmationScope _scope = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = new TurnConfirmationScope();
    }

    [Test]
    public void ANewScope_HasIssuedNothing()
    {
        _scope.IssuedTokens.ShouldBeEmpty();
        _scope.ProposalHintSkills.ShouldBeEmpty();
    }

    [Test]
    public void MarkIssued_RemembersTheTokenWithoutMakingItSensitive()
    {
        _scope.MarkIssued(GateToken);

        _scope.IssuedTokens.ShouldBe([GateToken]);
        _scope.WasIssuedThisTurnForSensitiveSkill(GateToken).ShouldBeFalse();
    }

    [Test]
    public void MarkIssuedForSensitiveSkill_KeepsItsOwnMeaningAndDoesNotRecordTheTokenAsIssued()
    {
        _scope.MarkIssuedForSensitiveSkill(SensitiveToken);

        _scope.WasIssuedThisTurnForSensitiveSkill(SensitiveToken).ShouldBeTrue();
        _scope.IssuedTokens.ShouldBeEmpty();
    }

    [Test]
    public void MarkIssued_TwiceForTheSameToken_KeepsOneEntry()
    {
        _scope.MarkIssued(GateToken);
        _scope.MarkIssued(GateToken);

        _scope.IssuedTokens.Count.ShouldBe(1);
    }

    [Test]
    public void MarkProposalHint_RemembersTheApplySkillIgnoringCase()
    {
        _scope.MarkProposalHint(ApplySkill);
        _scope.MarkProposalHint(ApplySkill.ToUpperInvariant());

        _scope.ProposalHintSkills.ShouldHaveSingleItem();
    }
}
