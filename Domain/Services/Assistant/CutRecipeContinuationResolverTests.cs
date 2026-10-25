// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for CutRecipeContinuationResolver: the turn that redeems a held seal_shift resumes the recipe
/// "dienst-aus-bestellung-schneiden" only when the immediately preceding turn was that recipe's request and created the
/// order through create_shift. Every other redemption (another skill, a superseded or missing record, a request that is
/// not the recipe, no created order) resumes nothing.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class CutRecipeContinuationResolverTests
{
    private const string OrderId = "33333333-3333-3333-3333-333333333333";
    private const string Request =
        "Erstelle eine 24/7-Bestellung fuer Kunde Weiss und schneide sie in 3 Dienste auf: 07:00-15:00, 15:00-23:00, 23:00-07:00.";

    [Test]
    public void ARedeemedSealAfterTheRecipeTurn_ResumesAtTheConfirmedReplay()
    {
        var plan = CutRecipeContinuationResolver.Resolve(RecipeConstants.SealShiftSkill, RecipeTurn());

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(RecipeConstants.CutFromOrderRecipeName);
        plan.CurrentSkill.ShouldBe(AutonomyDefaults.ConfirmPendingActionSkillName);
    }

    [Test]
    public void ARedeemedOtherSkill_ResumesNothing()
    {
        CutRecipeContinuationResolver.Resolve(RecipeConstants.CutShiftSkill, RecipeTurn()).ShouldBeNull();
        CutRecipeContinuationResolver.Resolve(null, RecipeTurn()).ShouldBeNull();
    }

    [Test]
    public void NoPrecedingRecord_ResumesNothing()
    {
        CutRecipeContinuationResolver.Resolve(RecipeConstants.SealShiftSkill, null).ShouldBeNull();
    }

    [Test]
    public void ASupersededRecord_ResumesNothing()
    {
        var turn = RecipeTurn();
        turn.SupersededAtUtc = DateTime.UtcNow;

        CutRecipeContinuationResolver.Resolve(RecipeConstants.SealShiftSkill, turn).ShouldBeNull();
    }

    [Test]
    public void APrecedingRequestThatIsNotTheRecipe_ResumesNothing()
    {
        var turn = RecipeTurn();
        turn.UserMessage = "Lege eine Bestellung fuer Kunde Weiss an.";

        CutRecipeContinuationResolver.Resolve(RecipeConstants.SealShiftSkill, turn).ShouldBeNull();
    }

    [Test]
    public void APrecedingTurnWithoutASuccessfulCreate_ResumesNothing()
    {
        var turn = RecipeTurn();
        turn.Calls = [CreateCall(success: false)];

        CutRecipeContinuationResolver.Resolve(RecipeConstants.SealShiftSkill, turn).ShouldBeNull();
    }

    [Test]
    public void ACreateWithoutAShiftId_ResumesNothing()
    {
        var turn = RecipeTurn();
        turn.Calls = [new AssistantLastActionCall
        {
            SkillName = RecipeConstants.CreateShiftSkill, Success = true, ResultDataJson = "{\"Name\":\"X\"}"
        }];

        CutRecipeContinuationResolver.Resolve(RecipeConstants.SealShiftSkill, turn).ShouldBeNull();
    }

    private static AssistantLastAction RecipeTurn() => new()
    {
        UserMessage = Request,
        Calls =
        [
            new AssistantLastActionCall { SkillName = RecipeConstants.FindCustomerSkill, Success = true },
            CreateCall(success: true)
        ]
    };

    private static AssistantLastActionCall CreateCall(bool success) => new()
    {
        SkillName = RecipeConstants.CreateShiftSkill,
        Success = success,
        ResultDataJson = $"{{\"ShiftId\":\"{OrderId}\",\"Name\":\"X\"}}"
    };
}
