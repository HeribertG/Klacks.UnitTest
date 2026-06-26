// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BulkAddShiftsToGroupSkill: a preview (apply=false) never persists, an apply writes the
/// new links inside a transaction and re-reads them to confirm (reporting added/verified), a failed
/// re-read rolls the whole batch back, and an unknown group name is rejected with the real group list.
/// </summary>

using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BulkAddShiftsToGroupSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMediator _mediator = null!;
    private BulkAddShiftsToGroupSkill _skill = null!;

    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid Shift1 = Guid.NewGuid();
    private static readonly Guid Shift2 = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mediator = Substitute.For<IMediator>();
        _skill = new BulkAddShiftsToGroupSkill(_groupRepository, _groupItemRepository, _unitOfWork, _mediator);

        _groupRepository.List().Returns(new List<Group> { new() { Id = BernGroupId, Name = "Bern" } });

        _mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TruncatedShiftResource
            {
                Shifts = new List<ShiftResource>
                {
                    new() { Id = Shift1, Name = "Nachtwache A" },
                    new() { Id = Shift2, Name = "Nachtwache B" }
                }
            });

        _groupItemRepository.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(2);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewGroups" }
    };

    private static Dictionary<string, object> Params(bool apply) => new()
    {
        ["groupName"] = "Bern",
        ["searchTerm"] = "Nachtwache",
        ["apply"] = apply
    };

    [Test]
    public async Task Preview_DoesNotPersist_AndListsMatches()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: false));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Preview"));
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task Apply_AddsAllMatches_AndReportsVerified()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _groupItemRepository.Received(2).Add(
            Arg.Is<GroupItem>(gi => gi.GroupId == BernGroupId && gi.ShiftId != null));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Apply_RollsBackByError_WhenVerificationCountDoesNotMatch()
    {
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
    }

    [Test]
    public async Task ReturnsError_ListingRealGroups_WhenGroupNameIsUnknown()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Zürich", ["searchTerm"] = "Nachtwache" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
        Assert.That(result.Message, Does.Contain("Bern"));
    }
}
