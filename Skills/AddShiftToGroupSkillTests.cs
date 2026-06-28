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

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddShiftToGroupSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
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
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new AddShiftToGroupSkill(_shiftRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock);

        _shiftRepository.Exists(ShiftId).Returns(true);
        _groupRepository.Get(GroupId).Returns(new Group { Id = GroupId, Name = "Bern" });
        _groupItemRepository.GetGroupIdsByShiftId(ShiftId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewGroups" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["shiftId"] = ShiftId.ToString(),
        ["groupId"] = GroupId.ToString()
    };

    [Test]
    public async Task Adds_AndReportsVerified_WhenPersistenceIsConfirmed()
    {
        _groupItemRepository.GetNoTracking(Arg.Any<Guid>())
            .Returns(ci => new GroupItem { Id = ci.Arg<Guid>(), ShiftId = ShiftId, GroupId = GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _groupItemRepository.Received(1).Add(
            Arg.Is<GroupItem>(gi => gi.ShiftId == ShiftId && gi.GroupId == GroupId));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_AndRollsBack_WhenPersistenceCannotBeConfirmed()
    {
        _groupItemRepository.GetNoTracking(Arg.Any<Guid>()).Returns((GroupItem?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
        await _groupItemRepository.Received(1).Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task Rejects_WhenShiftIsAlreadyInGroup()
    {
        _groupItemRepository.GetGroupIdsByShiftId(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already assigned"));
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
    }
}
