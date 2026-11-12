// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the thin find_replacement skill: it resolves the shift (errors when missing),
/// validates the analyseToken, answers a shift outside the caller's group visibility like a missing one,
/// dispatches FindReplacementQuery and projects the result. Candidate
/// selection / ranking logic is covered by FindReplacementQueryHandlerTests. The output for the model is cut:
/// the ten best eligible candidates in the order of the handler, three soft findings per candidate with the full
/// number next to them, the excluded as numbers per reason split into own group and borrowed, ten of them by
/// name with the own group first; the counts stay complete and the message says what was left out.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Notifications;
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

    [Test]
    public async Task TwelveEligible_ListsTheBestTenInTheOrderOfTheHandler_AndKeepsTheFullCount()
    {
        // Arrange
        var eligible = Eligible(12);
        SetResult(eligible, []);

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        var data = DataAsJson(result);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(12);
        data.GetProperty("UnlistedEligibleCount").GetInt32().ShouldBe(2);
        data.GetProperty("Candidates").EnumerateArray()
            .Select(c => c.GetProperty("ClientId").GetGuid())
            .ShouldBe(eligible.Take(10).Select(c => c.ClientId));
    }

    [Test]
    public async Task TwelveEligible_MessageSaysTheListIsShortenedAndHowManyMoreAreEligible()
    {
        // Arrange
        SetResult(Eligible(12), []);

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        result.Message.ShouldStartWith("12 eligible replacement(s)");
        result.Message.ShouldContain("The list is shortened: only the best 10 eligible are listed, 2 more are eligible");
    }

    [Test]
    public async Task FiveEligible_AreAllListed_WithoutAShorteningHint()
    {
        // Arrange
        var eligible = Eligible(5);
        SetResult(eligible, [new ExcludedCandidate(Guid.NewGuid(), "Anna", "absent")]);

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        var data = DataAsJson(result);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(5);
        data.GetProperty("UnlistedEligibleCount").GetInt32().ShouldBe(0);
        data.GetProperty("UnnamedExcludedCount").GetInt32().ShouldBe(0);
        data.GetProperty("Candidates").EnumerateArray()
            .Select(c => c.GetProperty("ClientId").GetGuid())
            .ShouldBe(eligible.Select(c => c.ClientId));
        result.Message.ShouldNotContain("shortened");
        result.Message.ShouldNotContain("more are");
    }

    [Test]
    public async Task CandidateWithFiveSoftFindings_ListsTheFirstThree_AndReportsTheFullNumber()
    {
        // Arrange
        var findings = Enumerable.Range(1, 5)
            .Select(i => new ScheduleValidationNotificationDto
            {
                Type = ScheduleValidationType.Warning,
                Comment = $"finding-{i}",
                Date = Date
            })
            .ToList();
        var twoFindings = findings.Take(2).ToList();
        SetResult(
            [
                new ReplacementCandidate(Guid.NewGuid(), "Cara", false, findings, 0m),
                new ReplacementCandidate(Guid.NewGuid(), "Dana", false, twoFindings, 0m)
            ],
            []);

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        var candidates = DataAsJson(result).GetProperty("Candidates");
        candidates[0].GetProperty("SoftConflictCount").GetInt32().ShouldBe(5);
        candidates[0].GetProperty("SoftConflicts").EnumerateArray()
            .Select(c => c.GetProperty("Comment").GetString())
            .ShouldBe(["finding-1", "finding-2", "finding-3"]);
        candidates[1].GetProperty("SoftConflictCount").GetInt32().ShouldBe(2);
        candidates[1].GetProperty("SoftConflicts").EnumerateArray()
            .Select(c => c.GetProperty("Comment").GetString())
            .ShouldBe(["finding-1", "finding-2"]);
    }

    [Test]
    public async Task AnErrorFindingBehindThreeWarnings_IsListedFirst_NotCutOff()
    {
        // Arrange: an overridden block finding is an Error that the handler reports after the warnings.
        var findings = Enumerable.Range(1, 3)
            .Select(i => new ScheduleValidationNotificationDto
            {
                Type = ScheduleValidationType.Warning,
                Comment = $"warning-{i}",
                Date = Date
            })
            .Append(new ScheduleValidationNotificationDto
            {
                Type = ScheduleValidationType.Error,
                Comment = "overridden-block",
                Date = Date
            })
            .ToList();
        SetResult([new ReplacementCandidate(Guid.NewGuid(), "Cara", false, findings, 0m)], []);

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        var candidate = DataAsJson(result).GetProperty("Candidates")[0];
        candidate.GetProperty("SoftConflictCount").GetInt32().ShouldBe(4);
        candidate.GetProperty("SoftConflicts").EnumerateArray()
            .Select(c => c.GetProperty("Comment").GetString())
            .ShouldBe(["overridden-block", "warning-1", "warning-2"]);
    }

    [Test]
    public async Task Excluded_AreCountedPerReason_SplitIntoOwnGroupAndBorrowed()
    {
        // Arrange
        SetResult(
            [],
            [
                Excluded("Own absent 1", "absent", borrowed: false),
                Excluded("Borrowed absent 1", "absent", borrowed: true),
                Excluded("Own absent 2", "absent", borrowed: false),
                Excluded("Borrowed collision 1", "collision", borrowed: true),
                Excluded("Borrowed absent 2", "absent", borrowed: true),
                Excluded("Borrowed absent 3", "absent", borrowed: true),
                Excluded("Own blacklisted", "blacklisted", borrowed: false),
                Excluded("Borrowed collision 2", "collision", borrowed: true)
            ]);

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        var data = DataAsJson(result);
        data.GetProperty("ExcludedCount").GetInt32().ShouldBe(8);
        data.GetProperty("ExcludedByReason").EnumerateArray()
            .Select(r => (
                r.GetProperty("Reason").GetString(),
                r.GetProperty("OwnGroup").GetInt32(),
                r.GetProperty("Borrowed").GetInt32()))
            .ShouldBe([("absent", 2, 3), ("collision", 0, 2), ("blacklisted", 1, 0)]);
    }

    [Test]
    public async Task FourteenExcluded_NamesTenWithTheOwnGroupFirst_AndSaysHowManyAreOnlyCounted()
    {
        // Arrange: the borrowed people come first in the result, so the own group only leads when the skill sorts.
        var borrowed = Enumerable.Range(1, 9).Select(i => Excluded($"Borrowed {i}", "absent", borrowed: true)).ToList();
        var own = Enumerable.Range(1, 5).Select(i => Excluded($"Own {i}", "collision", borrowed: false)).ToList();
        SetResult([], [.. borrowed, .. own]);

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        var data = DataAsJson(result);
        data.GetProperty("ExcludedCount").GetInt32().ShouldBe(14);
        data.GetProperty("UnnamedExcludedCount").GetInt32().ShouldBe(4);
        data.GetProperty("Excluded").EnumerateArray()
            .Select(e => (e.GetProperty("ClientId").GetGuid(), e.GetProperty("Borrowed").GetBoolean()))
            .ShouldBe([
                .. own.Select(e => (e.ClientId, false)),
                .. borrowed.Take(5).Select(e => (e.ClientId, true))
            ]);
        data.GetProperty("ExcludedByReason").EnumerateArray()
            .Select(r => (
                r.GetProperty("Reason").GetString(),
                r.GetProperty("OwnGroup").GetInt32(),
                r.GetProperty("Borrowed").GetInt32()))
            .ShouldBe([("absent", 0, 9), ("collision", 5, 0)]);
        result.Message.ShouldContain("Only 10 of the excluded are named, own group first; 4 more are counted per reason");
    }

    [Test]
    public async Task TenEligibleAndTenExcluded_AreAllListed_WithoutAShorteningHint()
    {
        // Arrange
        SetResult(
            Eligible(10),
            Enumerable.Range(1, 10).Select(i => Excluded($"Own {i}", "absent", borrowed: false)).ToList());

        // Act
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        // Assert
        var data = DataAsJson(result);
        data.GetProperty("Candidates").GetArrayLength().ShouldBe(10);
        data.GetProperty("Excluded").GetArrayLength().ShouldBe(10);
        data.GetProperty("UnlistedEligibleCount").GetInt32().ShouldBe(0);
        data.GetProperty("UnnamedExcludedCount").GetInt32().ShouldBe(0);
        result.Message.ShouldNotContain("shortened");
        result.Message.ShouldNotContain("more are");
    }

    private void SetResult(IReadOnlyList<ReplacementCandidate> eligible, IReadOnlyList<ExcludedCandidate> excluded)
        => _mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReplacementSearchResult(eligible, excluded));

    private static List<ReplacementCandidate> Eligible(int count)
        => Enumerable.Range(1, count)
            .Select(i => new ReplacementCandidate(Guid.NewGuid(), $"Candidate {i}", false, [], 0m))
            .ToList();

    private static ExcludedCandidate Excluded(string name, string reason, bool borrowed)
        => new(Guid.NewGuid(), name, reason, borrowed);
}
