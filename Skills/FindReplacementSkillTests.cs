// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the thin find_replacement skill: it resolves the shift (errors when missing),
/// validates the analyseToken, answers a shift outside the caller's group visibility like a missing one,
/// dispatches FindReplacementQuery and projects the result. Candidate
/// selection / ranking logic is covered by FindReplacementQueryHandlerTests.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class FindReplacementSkillTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 10);

    private IShiftRepository _shiftRepo = null!;
    private IMediator _mediator = null!;
    private IGroupVisibilityGuard _groupGuard = null!;

    [SetUp]
    public void Setup()
    {
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _shiftRepo = Substitute.For<IShiftRepository>();
        _shiftRepo.Get(ShiftId).Returns(new Shift
        {
            Id = ShiftId,
            Name = "Night",
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0)
        });

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReplacementSearchResult(
                new List<ReplacementCandidate> { new(Guid.NewGuid(), "Cara", false, [], 0m) },
                new List<ExcludedCandidate> { new(Guid.NewGuid(), "Anna", "absent") }));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private FindReplacementSkill Skill() => new(_shiftRepo, _mediator, _groupGuard);

    private static Dictionary<string, object> Params() => new()
    {
        ["shiftId"] = ShiftId.ToString(),
        ["date"] = Date,
        ["groupId"] = GroupId.ToString()
    };

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task DispatchesQuery_AndProjects()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(1);
        data.GetProperty("ExcludedCount").GetInt32().ShouldBe(1);
        data.GetProperty("ShiftName").GetString().ShouldBe("Night");

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q =>
                q.ShiftId == ShiftId && q.GroupId == GroupId && q.StartTime == new TimeOnly(22, 0)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ShiftNotFound_ReturnsError()
    {
        _shiftRepo.Get(ShiftId).Returns((Shift?)null);

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task ShiftOnlyInHiddenGroups_IsAnsweredLikeAMissingShift_WithoutLeakingItsName()
    {
        var hiddenGroup = Guid.NewGuid();
        var otherHiddenGroup = Guid.NewGuid();
        AttachShiftToGroups(hiddenGroup, otherHiddenGroup);
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var hidden = await Skill().ExecuteAsync(Ctx(), Params());
        _shiftRepo.Get(ShiftId).Returns((Shift?)null);
        var missing = await Skill().ExecuteAsync(Ctx(), Params());

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldBe(missing.Message);
        hidden.Message.ShouldNotContain("Night");
        hidden.Data.ShouldBeNull();
        await _mediator.DidNotReceive().Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ShiftWithAtLeastOneVisibleGroup_IsServed()
    {
        var hiddenGroup = Guid.NewGuid();
        var visibleGroup = Guid.NewGuid();
        AttachShiftToGroups(hiddenGroup, visibleGroup);
        _groupGuard.IsGroupVisibleAsync(hiddenGroup, Arg.Any<CancellationToken>()).Returns(false);
        _groupGuard.IsGroupVisibleAsync(visibleGroup, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("ShiftName").GetString().ShouldBe("Night");
    }

    [Test]
    public async Task DeletedMembershipInAHiddenGroup_DoesNotMakeAnUngroupedShiftInvisible()
    {
        var shift = AttachShiftToGroups(Guid.NewGuid());
        shift.GroupItems[0].IsDeleted = true;
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task UnrestrictedCaller_SeesShiftOfAnyGroup()
    {
        AttachShiftToGroups(Guid.NewGuid());
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("ShiftName").GetString().ShouldBe("Night");
    }

    private Shift AttachShiftToGroups(params Guid[] groupIds)
    {
        var shift = new Shift
        {
            Id = ShiftId,
            Name = "Night",
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0),
            GroupItems = groupIds.Select(id => new GroupItem { GroupId = id, ShiftId = ShiftId }).ToList()
        };
        _shiftRepo.Get(ShiftId).Returns(shift);
        return shift;
    }

    [Test]
    public async Task InvalidAnalyseToken_ReturnsError()
    {
        var p = Params();
        p["analyseToken"] = "not-a-guid";

        var result = await Skill().ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("analyseToken");
    }
}
