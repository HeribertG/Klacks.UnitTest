// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// ConditionApprovalRosterResolver per kind class and per eligibility rule: a shift-scoped kind puts the
/// last planner first only when that account exists, is not blocked, is a real person (not the
/// Anonymous/System audit sentinel) and holds the remediation skill's permissions; client-scoped kinds
/// and kinds without an EntityId never look at Work rows; a finding without a group asks the admins
/// only; the audience is rights-filtered and ordered by EscalationRosterOrder with admins held back to
/// the last stage; nobody appears twice.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class ConditionApprovalRosterResolverTests
{
    private const string LastPlannerId = "planner-last";
    private const string GroupPlannerId = "planner-group";
    private const string SecondGroupPlannerId = "planner-group-2";
    private const string AdminId = "admin-1";
    private const string SecondAdminId = "admin-2";
    private const string UnknownUserId = "user-vanished";
    private const string SkillOnlyPermission = "CanRunSpecialRemediation";

    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly string[] RequiresShiftCreate = [Permissions.CanCreateShifts];

    private IWorkRepository _workRepository = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IUserManagementService _userManagementService = null!;
    private ConditionApprovalRosterResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _userManagementService = Substitute.For<IUserManagementService>();

        _userManagementService.FindUserByIdAsync(Arg.Any<string>()).Returns(Task.FromResult<AppUser?>(null));
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { AdminId }));
        _audienceResolver.GetPlanningUserIdsForGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { AdminId, GroupPlannerId }));

        SetupUser(AdminId, rosterOrder: 9, blocked: false, Roles.Admin);
        SetupUser(GroupPlannerId, rosterOrder: 2, blocked: false, Roles.Authorised);

        _sut = new ConditionApprovalRosterResolver(
            _workRepository,
            _audienceResolver,
            _userManagementService,
            new SkillPermissionGate(_userManagementService),
            Substitute.For<ILogger<ConditionApprovalRosterResolver>>());
    }

    private AppUser SetupUser(string id, int rosterOrder, bool blocked, params string[] roles)
    {
        var user = new AppUser { Id = id, UserName = id, FirstName = id, EscalationRosterOrder = rosterOrder };
        _userManagementService.FindUserByIdAsync(id).Returns(Task.FromResult<AppUser?>(user));
        _userManagementService.IsAccountBlockedAsync(user).Returns(Task.FromResult(blocked));
        _userManagementService.GetUserRolesAsync(user).Returns(Task.FromResult<IList<string>>(roles.ToList()));
        return user;
    }

    private void SetupLastPlanner(string? actor) =>
        _workRepository.GetLastPlannerAuditActorForShiftAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(actor));

    private static AgentCondition Condition(string triggerKind, Guid? entityId, Guid? groupId) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = triggerKind,
        EntityId = entityId,
        GroupId = groupId
    };

    private static AgentCondition ShiftCondition() => Condition(AgentTriggerKinds.EmptyContainer, ShiftId, GroupId);

    private static string[] Ids(IReadOnlyList<EscalationRosterCandidate> roster) => roster.Select(c => c.UserId).ToArray();

    [Test]
    public async Task ShiftKind_ValidPlannerWithPermission_IsStageOne_ThenAudience_ThenAdmins()
    {
        SetupUser(LastPlannerId, rosterOrder: 5, blocked: false, Roles.Authorised);
        SetupLastPlanner(LastPlannerId);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { LastPlannerId, GroupPlannerId, AdminId }));
        Assert.That(roster[0].DisplayName, Is.EqualTo($"{LastPlannerId} ({LastPlannerId})"));
    }

    [Test]
    public async Task ShiftKind_PlannerIsAnonymousAuditActor_SkipsStageOne()
    {
        SetupLastPlanner(AuditActorNames.Anonymous);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
        await _userManagementService.DidNotReceive().FindUserByIdAsync(AuditActorNames.Anonymous);
    }

    [Test]
    public async Task ShiftKind_PlannerIsSystemAuditActor_SkipsStageOne()
    {
        SetupLastPlanner(AuditActorNames.System);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
    }

    [Test]
    public async Task ShiftKind_NoWorkEverRecorded_SkipsStageOne()
    {
        SetupLastPlanner(null);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
    }

    [Test]
    public async Task ShiftKind_PlannerWithoutRequiredPermission_SkipsStageOne()
    {
        SetupUser(LastPlannerId, rosterOrder: 1, blocked: false);
        SetupLastPlanner(LastPlannerId);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }), "The Planer floor does not include CanCreateShifts.");
    }

    [Test]
    public async Task ShiftKind_PlannerFloorUser_HoldsFloorPermission_IsStageOneEvenThoughNotInAudience()
    {
        SetupUser(LastPlannerId, rosterOrder: 1, blocked: false);
        SetupLastPlanner(LastPlannerId);

        var roster = await _sut.ResolveAsync(ShiftCondition(), [Permissions.CanEditSchedule]);

        Assert.That(Ids(roster), Is.EqualTo(new[] { LastPlannerId, GroupPlannerId, AdminId }));
    }

    [Test]
    public async Task ShiftKind_PlannerDeactivatedOrLockedOut_SkipsStageOne()
    {
        SetupUser(LastPlannerId, rosterOrder: 1, blocked: true, Roles.Authorised);
        SetupLastPlanner(LastPlannerId);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
    }

    [Test]
    public async Task ShiftKind_PlannerAccountNoLongerExists_SkipsStageOne()
    {
        SetupLastPlanner(UnknownUserId);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
    }

    [Test]
    public async Task ShiftKind_PlannerAlsoInAudienceAndAdmin_AppearsOnce()
    {
        SetupLastPlanner(AdminId);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { AdminId, GroupPlannerId }));
    }

    [Test]
    public async Task ClientKind_NeverConsultsWorkRows_AudienceOnly()
    {
        var condition = Condition(AgentTriggerKinds.AvailabilityGap, Guid.NewGuid(), GroupId);

        var roster = await _sut.ResolveAsync(condition, RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
        await _workRepository.DidNotReceiveWithAnyArgs().GetLastPlannerAuditActorForShiftAsync(default, default);
    }

    [Test]
    public async Task KindWithoutEntityId_NeverConsultsWorkRows_AudienceOnly()
    {
        var condition = Condition(AgentTriggerKinds.NextPeriodSchedulingDue, entityId: null, GroupId);

        var roster = await _sut.ResolveAsync(condition, RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
        await _workRepository.DidNotReceiveWithAnyArgs().GetLastPlannerAuditActorForShiftAsync(default, default);
    }

    [Test]
    public async Task FindingWithoutGroup_AdminsOnly()
    {
        SetupUser(LastPlannerId, rosterOrder: 1, blocked: false, Roles.Authorised);
        SetupLastPlanner(LastPlannerId);
        var condition = Condition(AgentTriggerKinds.EmptyContainer, ShiftId, groupId: null);

        var roster = await _sut.ResolveAsync(condition, RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { LastPlannerId, AdminId }));
        await _audienceResolver.DidNotReceiveWithAnyArgs().GetPlanningUserIdsForGroupAsync(default, default);
    }

    [Test]
    public async Task Audience_OrderedByEscalationRosterOrder_AdminsHeldBackToLastStage()
    {
        SetupUser(SecondGroupPlannerId, rosterOrder: 1, blocked: false, Roles.Authorised);
        SetupUser(SecondAdminId, rosterOrder: 0, blocked: false, Roles.Admin);
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { AdminId, SecondAdminId }));
        _audienceResolver.GetPlanningUserIdsForGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { AdminId, SecondAdminId, GroupPlannerId, SecondGroupPlannerId }));
        SetupLastPlanner(null);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { SecondGroupPlannerId, GroupPlannerId, SecondAdminId, AdminId }));
    }

    [Test]
    public async Task Audience_BlockedMember_IsExcluded()
    {
        SetupUser(SecondGroupPlannerId, rosterOrder: 1, blocked: true, Roles.Authorised);
        _audienceResolver.GetPlanningUserIdsForGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { AdminId, GroupPlannerId, SecondGroupPlannerId }));
        SetupLastPlanner(null);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(Ids(roster), Is.EqualTo(new[] { GroupPlannerId, AdminId }));
    }

    [Test]
    public async Task AdminBypass_AdminPassesSkillOnlyPermission_AuthorisedPlannerDoesNot()
    {
        SetupLastPlanner(null);

        var roster = await _sut.ResolveAsync(ShiftCondition(), [SkillOnlyPermission]);

        Assert.That(Ids(roster), Is.EqualTo(new[] { AdminId }));
    }

    [Test]
    public async Task NoEligibleCandidateAnywhere_ReturnsEmptyRoster()
    {
        SetupUser(AdminId, rosterOrder: 9, blocked: true, Roles.Admin);
        SetupUser(GroupPlannerId, rosterOrder: 2, blocked: true, Roles.Authorised);
        SetupLastPlanner(null);

        var roster = await _sut.ResolveAsync(ShiftCondition(), RequiresShiftCreate);

        Assert.That(roster, Is.Empty);
    }
}
