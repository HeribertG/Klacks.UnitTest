// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RemoveShiftFromGroupSkill: the linking group_item row is deleted inside a transaction
/// and re-read from the database; a confirmed missing read reports a verified success, and a shift that
/// is not currently linked to the group is rejected up front without touching the database.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class RemoveShiftFromGroupSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private RemoveShiftFromGroupSkill _skill = null!;

    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid GroupItemId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new RemoveShiftFromGroupSkill(
            _shiftRepository, _groupRepository, TestGroupScopeGuard.Unrestricted(), _groupItemRepository, _unitOfWork);

        _shiftRepository.Exists(ShiftId).Returns(true);
        _groupRepository.Get(GroupId).Returns(new Group { Id = GroupId, Name = "Bern" });
        _groupItemRepository.GetQuery().Returns(new TestAsyncEnumerable<GroupItem>(
            new List<GroupItem> { new() { Id = GroupItemId, ShiftId = ShiftId, GroupId = GroupId } }));
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
    public async Task Removes_AndReportsVerified_WhenDeletionIsConfirmed()
    {
        _groupItemRepository.GetNoTracking(GroupItemId).Returns((GroupItem?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _groupItemRepository.Received(1).Delete(GroupItemId);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_AndRollsBack_WhenDeletionCannotBeConfirmed()
    {
        _groupItemRepository.GetNoTracking(GroupItemId)
            .Returns(new GroupItem { Id = GroupItemId, ShiftId = ShiftId, GroupId = GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
        await _groupItemRepository.Received(1).Delete(GroupItemId);
    }

    [Test]
    public async Task Rejects_WhenShiftIsNotLinkedToGroup()
    {
        _groupItemRepository.GetQuery().Returns(new TestAsyncEnumerable<GroupItem>(new List<GroupItem>()));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not linked"));
        await _groupItemRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }
}
