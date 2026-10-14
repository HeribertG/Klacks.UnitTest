// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End to end across turns with the REAL gate, the REAL preview provider and skill of bulk_delete_works, the REAL confirm
/// skill, the REAL risk classifier and the real pending store (only the planner and the mediator are doubles): turn one
/// holds the call and stores the binding of the previewed works; turn two redeems it with an explicit yes and deletes
/// exactly once when the works are still the ones that were shown. When the list changed in between, the redemption
/// deletes nothing, the token is spent, the model's next call is held again with a fresh preview and a fresh token that
/// cannot be redeemed in the turn that issued it (A8), and a token sent along with the call itself is refused and stays
/// valid. A container child (child work or break) that appears under a listed work between the preview and the yes counts
/// as a change too. A call that skips the gate (a plan step) never deletes and says why.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConfirmationBindingPipelineTests
{
    private const string ConfirmationTokenKey = "confirmationToken";

    private IAgentAutonomyPreferenceRepository _preferences = null!;
    private IPendingConfirmationStore _store = null!;
    private IBulkWorkDeletionPlanner _planner = null!;
    private IMediator _mediator = null!;
    private List<PlannedWorkDeletion> _works = null!;
    private List<Guid> _childWorkIds = null!;
    private BulkDeleteWorksSkill _skill = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _preferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _preferences.GetAsync(_userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = _userId.ToString(), Level = AutonomyLevel.FullyAutonomous });
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => new BulkWorksResponse
            {
                SuccessCount = call.Arg<BulkDeleteWorksCommand>().Request.WorkIds.Count,
                DeletedIds = call.Arg<BulkDeleteWorksCommand>().Request.WorkIds.ToList()
            });
        _works = [NewWork(2), NewWork(3)];
        _childWorkIds = [];
        _planner = Substitute.For<IBulkWorkDeletionPlanner>();
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new BulkWorkDeletionPlan(null, "Max Müller", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), _works.ToList())
            {
                ChildWorkIds = _childWorkIds.ToList()
            });
        _skill = new BulkDeleteWorksSkill(_mediator, _planner, Substitute.For<IWorkRepository>());
    }

    private static PlannedWorkDeletion NewWork(int day) =>
        new(Guid.NewGuid(), new DateOnly(2026, 11, day), Guid.NewGuid(), "Early", new TimeOnly(6, 0), new TimeOnly(14, 0));

    private static SkillDescriptor Descriptor() =>
        new(BulkDeleteWorksSkill.SkillName, "test", SkillCategory.Crud, [], [], [], null);

    private static Dictionary<string, object> CallParameters() => new()
    {
        ["firstName"] = "Max",
        ["lastName"] = "Müller",
        ["fromDate"] = "2026-11-01",
        ["untilDate"] = "2026-11-30"
    };

    private SkillExecutionContext Context(string userMessage) => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSchedule" },
        UserConfirmation = AffirmationDetector.IsExplicitConfirmation(userMessage)
            ? UserConfirmationEvidence.ExplicitConfirmationReply
            : UserConfirmationEvidence.None
    };

    private (AutonomyGateService Gate, ConfirmPendingActionSkill Confirm) NewTurn()
    {
        var scope = new TurnConfirmationScope();
        var guard = new ConfirmationRedemptionGuard(scope);
        var gate = new AutonomyGateService(
            _preferences, new SkillRiskClassifier(), _store, scope, guard,
            [new BulkWorkDeletionConfirmationPreviewProvider(_planner)], NullLogger<AutonomyGateService>.Instance);
        var executor = Substitute.For<ISkillExecutor>();
        executor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => _skill.ExecuteAsync(
                call.Arg<SkillExecutionContext>(), call.Arg<SkillInvocation>().Parameters.ToDictionary(kv => kv.Key, kv => kv.Value)));
        return (gate, new ConfirmPendingActionSkill(_store, executor, guard));
    }

    private static Dictionary<string, object> Redeem(string token) =>
        new() { [AutonomyDefaults.ConfirmationTokenParameter] = token };

    private async Task<string> HoldAsync(AutonomyGateService gate)
    {
        var held = await gate.CheckAsync(Descriptor(), Context("Lösche alle Dienste von Müller im November"), CallParameters());

        held.ShouldNotBeNull();
        held.Type.ShouldBe(SkillResultType.Confirmation);
        held.Message!.ShouldContain("Delete 2 work(s)");
        return (string)held.Metadata![ConfirmationTokenKey];
    }

    [Test]
    public async Task UnchangedWorks_AreDeletedExactlyOnce_WhenTheUserConfirmsInTheNextTurn()
    {
        var token = await HoldAsync(NewTurn().Gate);

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue(confirm.Message);
        await _mediator.Received(1).Send(
            Arg.Is<BulkDeleteWorksCommand>(c => c.Request.WorkIds.SequenceEqual(_works.Select(w => w.WorkId))),
            Arg.Any<CancellationToken>());

        var again = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        again.Success.ShouldBeFalse();
        await _mediator.Received(1).Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWorkAddedAfterThePreview_IsNotDeleted_TheTokenIsSpent_AndTheNextCallIsHeldAgainWithAFreshPreview()
    {
        var token = await HoldAsync(NewTurn().Gate);
        _works.Add(NewWork(4));

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());

        var spent = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        spent.Success.ShouldBeFalse();

        var turnThree = NewTurn();
        var freshHold = await turnThree.Gate.CheckAsync(Descriptor(), Context("ja, zeig die neue Liste"), CallParameters());
        freshHold.ShouldNotBeNull();
        freshHold.Type.ShouldBe(SkillResultType.Confirmation);
        freshHold.Message!.ShouldContain("Delete 3 work(s)");
        var freshToken = (string)freshHold.Metadata![ConfirmationTokenKey];
        freshToken.ShouldNotBe(token);

        var sameTurn = await turnThree.Confirm.ExecuteAsync(Context("ja"), Redeem(freshToken));
        sameTurn.Success.ShouldBeFalse();
        sameTurn.Message.ShouldBe(ConfirmationRedemptionMessages.IssuedThisTurn);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());

        var laterConfirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(freshToken));
        laterConfirm.Success.ShouldBeTrue(laterConfirm.Message);
        await _mediator.Received(1).Send(
            Arg.Is<BulkDeleteWorksCommand>(c => c.Request.WorkIds.Count == 3), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AContainerChildAppearingAfterThePreview_IsNotDeleted_AndTheNextPreviewNamesIt()
    {
        _childWorkIds.Add(Guid.NewGuid());
        var turnOne = NewTurn();
        var held = await turnOne.Gate.CheckAsync(Descriptor(), Context("Lösche alle Dienste von Müller im November"), CallParameters());
        held.ShouldNotBeNull();
        held.Message!.ShouldContain("together with 1 container entry");
        var token = (string)held.Metadata![ConfirmationTokenKey];
        _childWorkIds.Add(Guid.NewGuid());

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldContain("3 item(s) were shown, 4 apply now (1 new, 0 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());

        var fresh = await NewTurn().Gate.CheckAsync(Descriptor(), Context("ja, zeig die neue Liste"), CallParameters());
        fresh.ShouldNotBeNull();
        fresh.Message!.ShouldContain("together with 2 container entries");
    }

    [Test]
    public async Task AWorkGoneAfterThePreview_IsNotDeleted()
    {
        var token = await HoldAsync(NewTurn().Gate);
        _works.RemoveAt(0);

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldContain("(0 new, 1 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ATokenSentWithTheCallItself_IsRefused_AndStillRedeemsThroughTheConfirmSkill()
    {
        var token = await HoldAsync(NewTurn().Gate);
        var parameters = CallParameters();
        parameters[AutonomyDefaults.ConfirmationTokenParameter] = token;

        var direct = await NewTurn().Gate.CheckAsync(Descriptor(), Context("ja"), parameters);

        direct.ShouldNotBeNull();
        direct.Type.ShouldBe(SkillResultType.Error);
        direct.Message.ShouldBe(string.Format(ConfirmationBindingMessages.DirectRedemptionRefused, BulkDeleteWorksSkill.SkillName));

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));
        confirm.Success.ShouldBeTrue(confirm.Message);
        await _mediator.Received(1).Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACallThatSkipsTheGate_LikeAPlanStep_NeverDeletes()
    {
        var context = Context("ja") with { BypassAutonomyGate = true };

        var result = await _skill.ExecuteAsync(context, CallParameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, BulkDeleteWorksSkill.SkillName));
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }
}
