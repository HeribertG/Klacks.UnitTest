// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End to end across turns with the REAL gate, the REAL preview providers, planner and skills of seal_open_orders and
/// seal_shift, the REAL confirm skill, the REAL risk classifier and the real pending store (only the mediator, the
/// repositories and the group scope are doubles). A held seal carries the server preview of exactly what would be sealed;
/// it is sealed once, after an explicit yes in a later turn. It is never redeemed in the turn that held it, never by the
/// review request the welcome toast and the order goals send, and never when an order appeared or changed classification
/// between the preview and the yes. A held apply=false preview replays as a preview and seals nothing. A call that skips
/// the gate (a plan step) never seals.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SealConfirmationBindingPipelineTests
{
    private const string ConfirmationTokenKey = "confirmationToken";
    private const string ReviewRequest = "Geh die offenen Bestellungen mit mir durch, bevor etwas versiegelt wird.";

    private IAgentAutonomyPreferenceRepository _preferences = null!;
    private IPendingConfirmationStore _store = null!;
    private IMediator _mediator = null!;
    private IShiftRepository _shiftRepository = null!;
    private SealOpenOrdersPlanner _planner = null!;
    private SealOpenOrdersSkill _sealOpenOrders = null!;
    private SealShiftSkill _sealShift = null!;
    private OrderSealingService _orderSealingService = null!;
    private List<Guid> _sealable = null!;
    private List<Guid> _blocked = null!;
    private List<SealOpenOrdersCommand> _applied = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _preferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _preferences.GetAsync(_userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = _userId.ToString(), Level = AutonomyLevel.FullyAutonomous });
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _sealable = [Guid.NewGuid(), Guid.NewGuid()];
        _blocked = [Guid.NewGuid()];
        _applied = [];

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => Result(call.Arg<SealOpenOrdersCommand>()));

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(new List<Group>());
        var groupScopeGuard = Substitute.For<IGroupScopeGuard>();
        groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        _planner = new SealOpenOrdersPlanner(groupRepository, groupScopeGuard, _mediator, companyClock);
        _sealOpenOrders = new SealOpenOrdersSkill(_planner, _mediator, Substitute.For<ISealOpenOrdersJobQueue>());

        _shiftRepository = Substitute.For<IShiftRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>()).Returns(ci => ci.Arg<Func<Task<Guid>>>()());
        _orderSealingService = new OrderSealingService(_shiftRepository, unitOfWork, ShiftVisibilityGuards.AllowAll());
        _sealShift = new SealShiftSkill(_orderSealingService);
    }

    private SealOpenOrdersResult Result(SealOpenOrdersCommand command)
    {
        if (command.Apply)
        {
            _applied.Add(command);
        }

        return new SealOpenOrdersResult(
            Applied: command.Apply, TotalOrders: _sealable.Count + _blocked.Count, SealableCount: _sealable.Count,
            SealedCount: command.Apply ? _sealable.Count : 0, BlockedCount: _blocked.Count, FailedCount: 0,
            BlockedOnlyByMissingGroupCount: 0, AutoAssignedCount: 0, AutoAssignRequested: false,
            SealedSample: [], BlockedSample: [], Failures: [])
        {
            Selection = OpenOrderSealingSelection.Of(_sealable, _blocked)
        };
    }

    private static SkillDescriptor Descriptor(string name) => new(name, "test", SkillCategory.Crud, [], [], [], null);

    private SkillExecutionContext Context(string userMessage) => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserLanguage = "de",
        UserPermissions = Permissions.ExpandRoles(Roles.Admin),
        UserConfirmation = AffirmationDetector.IsExplicitConfirmation(userMessage, "de")
            ? UserConfirmationEvidence.ExplicitConfirmationReply
            : UserConfirmationEvidence.None
    };

    private (AutonomyGateService Gate, ConfirmPendingActionSkill Confirm) NewTurn()
    {
        var scope = new TurnConfirmationScope();
        var guard = new ConfirmationRedemptionGuard(scope);
        var gate = new AutonomyGateService(
            _preferences, new SkillRiskClassifier(), _store, scope, guard,
            [new SealOpenOrdersConfirmationPreviewProvider(_planner), new SealShiftConfirmationPreviewProvider(_orderSealingService)],
            NullLogger<AutonomyGateService>.Instance);
        var executor = Substitute.For<ISkillExecutor>();
        executor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var invocation = call.Arg<SkillInvocation>();
                var parameters = invocation.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);
                return invocation.SkillName == SealShiftSkill.SkillName
                    ? _sealShift.ExecuteAsync(call.Arg<SkillExecutionContext>(), parameters)
                    : _sealOpenOrders.ExecuteAsync(call.Arg<SkillExecutionContext>(), parameters);
            });
        return (gate, new ConfirmPendingActionSkill(_store, executor, guard));
    }

    private static Dictionary<string, object> Redeem(string token) =>
        new() { [AutonomyDefaults.ConfirmationTokenParameter] = token };

    private static Dictionary<string, object> Apply() => new() { [PreviewApplySkillCalls.ApplyParameter] = true };

    private async Task<(string Token, string Message)> HoldAsync(
        AutonomyGateService gate, string skillName, Dictionary<string, object> parameters)
    {
        var held = await gate.CheckAsync(Descriptor(skillName), Context(ReviewRequest), parameters);

        held.ShouldNotBeNull();
        held.Type.ShouldBe(SkillResultType.Confirmation);
        return ((string)held.Metadata![ConfirmationTokenKey], held.Message!);
    }

    [Test]
    public async Task HeldApply_ShowsTheServerPreview_AndSealsOnce_AfterAnExplicitYesInTheNextTurn()
    {
        var (token, message) = await HoldAsync(NewTurn().Gate, SealOpenOrdersSkill.SkillName, Apply());

        message.ShouldContain("2 of 3 open order(s) can be sealed");
        message.ShouldContain("can never be undone");
        _applied.ShouldBeEmpty();

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue(confirm.Message);
        _applied.Count.ShouldBe(1);
        _applied[0].ExpectedSelection!.SealableIds.ShouldBe(_sealable, ignoreOrder: true);
        _applied[0].ExpectedSelection!.BlockedIds.ShouldBe(_blocked, ignoreOrder: true);
    }

    [Test]
    public async Task HeldApply_IsNeverRedeemedInTheTurnThatHeldIt()
    {
        var turn = NewTurn();
        var (token, _) = await HoldAsync(turn.Gate, SealOpenOrdersSkill.SkillName, Apply());

        var sameTurn = await turn.Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        sameTurn.Success.ShouldBeFalse();
        _applied.ShouldBeEmpty();
    }

    [Test]
    public async Task TheReviewRequest_NeverRedeemsAHeldSeal()
    {
        var (token, _) = await HoldAsync(NewTurn().Gate, SealOpenOrdersSkill.SkillName, Apply());

        var review = await NewTurn().Confirm.ExecuteAsync(Context(ReviewRequest), Redeem(token));

        review.Success.ShouldBeFalse();
        _applied.ShouldBeEmpty();
    }

    [Test]
    public async Task AnOrderThatAppearedBeforeTheYes_SealsNothing()
    {
        var (token, _) = await HoldAsync(NewTurn().Gate, SealOpenOrdersSkill.SkillName, Apply());
        _sealable.Add(Guid.NewGuid());

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeFalse();
        confirm.Message.ShouldContain("3 item(s) were shown, 4 apply now (1 new, 0 no longer there)");
        _applied.ShouldBeEmpty();
    }

    [Test]
    public async Task AHeldPreview_ReplaysAsAPreview_AndSealsNothing()
    {
        var (token, message) = await HoldAsync(NewTurn().Gate, SealOpenOrdersSkill.SkillName, new Dictionary<string, object>());

        message.ShouldContain("Preview only, this call seals nothing");

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue(confirm.Message);
        confirm.Message!.ShouldContain("Nothing was changed yet");
        _applied.ShouldBeEmpty();
    }

    [Test]
    public async Task APlanStep_NeverSeals()
    {
        var planStep = Context("ja") with { BypassAutonomyGate = true };

        var openOrders = await _sealOpenOrders.ExecuteAsync(planStep, Apply());
        var single = await _sealShift.ExecuteAsync(planStep, new() { [SealShiftSkill.ShiftIdParameter] = Guid.NewGuid().ToString() });

        openOrders.Success.ShouldBeFalse();
        single.Success.ShouldBeFalse();
        _applied.ShouldBeEmpty();
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task HeldSealShift_ShowsTheOrder_AndSealsItAfterAnExplicitYesInTheNextTurn()
    {
        var order = SealableOrder();
        var plannableShiftId = Guid.NewGuid();
        _shiftRepository.Get(order.Id).Returns(order);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });
        _shiftRepository.GetNoTracking(order.Id).Returns(new Shift { Id = order.Id, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(plannableShiftId)
            .Returns(new Shift { Id = plannableShiftId, OriginalId = order.Id, Status = ShiftStatus.OriginalShift });

        var (token, message) = await HoldAsync(
            NewTurn().Gate, SealShiftSkill.SkillName, new() { [SealShiftSkill.ShiftIdParameter] = order.Id.ToString() });

        message.ShouldContain("Seal the order 'Early Bern' (EB), 2026-06-01 to open end, 06:00-14:00");
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());

        var confirm = await NewTurn().Confirm.ExecuteAsync(Context("ja"), Redeem(token));

        confirm.Success.ShouldBeTrue(confirm.Message);
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == order.Id));
    }

    [Test]
    public async Task SealShift_OfAnOrderThatCannotBeSealed_IsRefusedBeforeAnyTokenIsIssued()
    {
        var order = SealableOrder();
        order.GroupItems = new List<GroupItem>();
        _shiftRepository.Get(order.Id).Returns(order);

        var held = await NewTurn().Gate.CheckAsync(
            Descriptor(SealShiftSkill.SkillName), Context(ReviewRequest),
            new Dictionary<string, object> { [SealShiftSkill.ShiftIdParameter] = order.Id.ToString() });

        held.ShouldNotBeNull();
        held.Type.ShouldNotBe(SkillResultType.Confirmation);
        held.Message.ShouldContain("at least one group");
    }

    private static Shift SealableOrder() => new()
    {
        Id = Guid.NewGuid(),
        Status = ShiftStatus.OriginalOrder,
        Name = "Early Bern",
        Abbreviation = "EB",
        FromDate = new DateOnly(2026, 6, 1),
        StartShift = new TimeOnly(6, 0),
        EndShift = new TimeOnly(14, 0),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 2,
        GroupItems = new List<GroupItem> { new() { Id = Guid.NewGuid(), GroupId = Guid.NewGuid(), IsDeleted = false } }
    };
}
