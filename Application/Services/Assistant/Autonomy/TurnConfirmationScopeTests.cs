// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The scope of one chat turn remembers which confirmation tokens and proposal hints the turn issued, so a
/// stopped turn can drop them and neither redemption path can use a token in the turn that issued it. The memory
/// is generic: whatever the risk class of the held skill, an issued token counts as issued this turn.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;

namespace Klacks.UnitTest.Application.Services.Assistant.Autonomy;

[TestFixture]
public class TurnConfirmationScopeTests
{
    private const string GateToken = "gate-token";
    private const string OtherToken = "other-token";
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
        _scope.WasIssuedThisTurn(GateToken).ShouldBeFalse();
    }

    [Test]
    public void MarkIssued_RemembersTheTokenAsIssuedThisTurn()
    {
        _scope.MarkIssued(GateToken);

        _scope.IssuedTokens.ShouldBe([GateToken]);
        _scope.WasIssuedThisTurn(GateToken).ShouldBeTrue();
        _scope.WasIssuedThisTurn(OtherToken).ShouldBeFalse();
    }

    [Test]
    public void WasIssuedThisTurn_ComparesTheTokenCaseSensitively()
    {
        _scope.MarkIssued(GateToken);

        _scope.WasIssuedThisTurn(GateToken.ToUpperInvariant()).ShouldBeFalse();
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
