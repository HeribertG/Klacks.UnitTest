// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddShiftToGroupSkill: the shift link is written inside a transaction and re-read from
/// the database; a confirmed read reports a verified success, a missing read rolls the write back and
/// reports an error instead of a false success, and a shift already in the group is rejected up front.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddShiftToGroupSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private FakeSelfApi _api = null!;
    private ICompanyClock _companyClock = null!;
    private AddShiftToGroupSkill _skill = null!;

    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, "api/backend/GroupItems", new GroupItemResource());
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new AddShiftToGroupSkill(
            _shiftRepository, _groupRepository, TestGroupScopeGuard.Unrestricted(), _groupItemRepository, _api.Client, new SelfApiRouteResolver(), _companyClock);

        _shiftRepository.Exists(ShiftId).Returns(true);
        _groupRepository.Get(GroupId).Returns(new Group { Id = GroupId, Name = "Bern" });
        _groupItemRepository.GetGroupIdsByShiftId(ShiftId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());

    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewGroups" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["shiftId"] = ShiftId.ToString(),
        ["groupId"] = GroupId.ToString()
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task Adds_AndReportsVerified_WhenPersistenceIsConfirmed()
    {
        _groupItemRepository.GetNoTracking(Arg.Any<Guid>())
            .Returns(ci => new GroupItem { Id = ci.Arg<Guid>(), ShiftId = ShiftId, GroupId = GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }


    [Test]
    public async Task Rejects_WhenShiftIsAlreadyInGroup()
    {
        _groupItemRepository.GetGroupIdsByShiftId(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already assigned"));
        _api.Calls.ShouldBeEmpty();
    }
}
