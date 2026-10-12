// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The decision behind both redemption paths of a held action: a token is never redeemable in the turn that issued it,
/// whatever the risk class, and in a later turn only with proof that the user answered (the chat's explicit yes; no client
/// endpoint and no MCP call carries it). The guard only decides; it consumes nothing.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant.Autonomy;

[TestFixture]
public class ConfirmationRedemptionGuardTests
{
    private const string Token = "token-1";

    private TurnConfirmationScope _scope = null!;
    private ConfirmationRedemptionGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = new TurnConfirmationScope();
        _guard = new ConfirmationRedemptionGuard(_scope);
    }

    private static SkillExecutionContext Context(UserConfirmationEvidence evidence) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        UserConfirmation = evidence
    };

    [TestCase(UserConfirmationEvidence.None)]
    [TestCase(UserConfirmationEvidence.ExplicitConfirmationReply)]
    public void ATokenIssuedThisTurn_IsRefused_WhateverTheEvidence(UserConfirmationEvidence evidence)
    {
        _scope.MarkIssued(Token);

        _guard.Evaluate(Token, Context(evidence)).ShouldBe(ConfirmationRedemptionVerdict.IssuedThisTurn);
    }

    [Test]
    public void ALaterTurn_WithoutEvidence_IsRefused()
    {
        _guard.Evaluate(Token, Context(UserConfirmationEvidence.None))
            .ShouldBe(ConfirmationRedemptionVerdict.UserConfirmationMissing);
    }

    [TestCase(UserConfirmationEvidence.ExplicitConfirmationReply)]
    public void ALaterTurn_WithEvidence_IsAllowed(UserConfirmationEvidence evidence)
    {
        _guard.Evaluate(Token, Context(evidence)).ShouldBe(ConfirmationRedemptionVerdict.Allowed);
    }

    [Test]
    public void ADefaultContext_CarriesNoEvidence()
    {
        Context(default).UserConfirmation.ShouldBe(UserConfirmationEvidence.None);
    }

    [Test]
    public void EveryRefusingVerdict_HasAModelFacingMessage()
    {
        ConfirmationRedemptionMessages.For(ConfirmationRedemptionVerdict.IssuedThisTurn)
            .ShouldBe(ConfirmationRedemptionMessages.IssuedThisTurn);
        ConfirmationRedemptionMessages.For(ConfirmationRedemptionVerdict.UserConfirmationMissing)
            .ShouldBe(ConfirmationRedemptionMessages.UserConfirmationMissing);
    }

    [Test]
    public void AskingForTheMessageOfTheAllowedVerdict_IsAProgrammingError()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => ConfirmationRedemptionMessages.For(ConfirmationRedemptionVerdict.Allowed));
        Should.Throw<ArgumentOutOfRangeException>(
            () => ConfirmationRedemptionMessages.For((ConfirmationRedemptionVerdict)999));
    }

    [Test]
    public void AnotherTokenIssuedThisTurn_DoesNotBlockThisOne()
    {
        _scope.MarkIssued("some-other-token");

        _guard.Evaluate(Token, Context(UserConfirmationEvidence.ExplicitConfirmationReply))
            .ShouldBe(ConfirmationRedemptionVerdict.Allowed);
    }
}
