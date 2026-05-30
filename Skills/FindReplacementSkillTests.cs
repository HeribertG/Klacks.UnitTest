// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the find_replacement skill: hard-exclusion on collision / rest-time / blacklist,
/// soft-ranking by aggregate findings (less headroom -> lower rank), preferred-first ordering, and
/// parameter/shift validation. Data payload asserted via its serialized JSON shape.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class FindReplacementSkillTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 10);

    private IShiftRepository _shiftRepo = null!;
    private IClientRepository _clientRepo = null!;
    private IPreCommitConflictChecker _checker = null!;
    private IClientShiftPreferenceRepository _prefRepo = null!;
    private IScheduleEntriesService _scheduleEntries = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepo = Substitute.For<IShiftRepository>();
        _shiftRepo.Get(ShiftId).Returns(new Shift
        {
            Id = ShiftId,
            Name = "Night",
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0)
        });

        _clientRepo = Substitute.For<IClientRepository>();
        _checker = Substitute.For<IPreCommitConflictChecker>();
        _prefRepo = Substitute.For<IClientShiftPreferenceRepository>();
        _prefRepo.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientShiftPreference>());

        _scheduleEntries = Substitute.For<IScheduleEntriesService>();
        SetOnLeave();
    }

    private void SetOnLeave(params ScheduleCell[] breakCells)
        => _scheduleEntries.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(breakCells));

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static Client Member(Guid id, string name)
        => new() { Id = id, Name = name, FirstName = string.Empty };

    private static ScheduleValidationNotificationDto Conflict(Guid clientId, ScheduleValidationType type, string comment)
        => new() { Type = type, ClientId = clientId, Date = Date, Comment = comment };

    private void SetMembers(params Client[] members)
        => _clientRepo.GetActiveClientsWithAddressesForGroupsAsync(
                Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(members.ToList());

    private void SetConflicts(params ScheduleValidationNotificationDto[] conflicts)
        => _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts.ToList()));

    private FindReplacementSkill Skill() => new(_shiftRepo, _clientRepo, _checker, _prefRepo, _scheduleEntries);

    private static Dictionary<string, object> Params() => new()
    {
        ["shiftId"] = ShiftId.ToString(),
        ["date"] = Date,
        ["groupId"] = GroupId.ToString()
    };

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task ExcludesCollision_RanksCleanBeforeSoft()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        SetMembers(Member(a, "Anna"), Member(b, "Bob"), Member(c, "Cara"));
        SetConflicts(
            Conflict(a, ScheduleValidationType.Error, "schedule.error-list.collision"),
            Conflict(b, ScheduleValidationType.Warning, "schedule.error-list.weekly-overtime"));

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(2);
        data.GetProperty("ExcludedCount").GetInt32().ShouldBe(1);

        var candidates = data.GetProperty("Candidates").EnumerateArray().ToList();
        candidates[0].GetProperty("ClientId").GetGuid().ShouldBe(c); // clean ranks before soft
        candidates[0].GetProperty("SoftConflictCount").GetInt32().ShouldBe(0);
        candidates[1].GetProperty("ClientId").GetGuid().ShouldBe(b);
        candidates[1].GetProperty("SoftConflictCount").GetInt32().ShouldBe(1);

        var excluded = data.GetProperty("Excluded").EnumerateArray().Single();
        excluded.GetProperty("ClientId").GetGuid().ShouldBe(a);
        excluded.GetProperty("Reason").GetString().ShouldBe("schedule.error-list.collision");
    }

    [Test]
    public async Task RestViolation_IsHardExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(Conflict(a, ScheduleValidationType.Warning, "schedule.error-list.rest-violation"));

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        var data = DataAsJson(result);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(0);
        data.GetProperty("Excluded").EnumerateArray().Single()
            .GetProperty("Reason").GetString().ShouldBe("schedule.error-list.rest-violation");
    }

    [Test]
    public async Task Blacklisted_IsExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts();
        _prefRepo.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientShiftPreference>
            {
                new() { ClientId = a, ShiftId = ShiftId, PreferenceType = ShiftPreferenceType.Blacklist }
            });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        var data = DataAsJson(result);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(0);
        data.GetProperty("Excluded").EnumerateArray().Single()
            .GetProperty("Reason").GetString().ShouldBe("blacklisted");
    }

    [Test]
    public async Task Preferred_RanksFirst_EvenWithSoftConflict()
    {
        var preferred = Guid.NewGuid();
        var clean = Guid.NewGuid();
        SetMembers(Member(preferred, "Pia"), Member(clean, "Cara"));
        SetConflicts(Conflict(preferred, ScheduleValidationType.Warning, "schedule.error-list.overtime"));
        _prefRepo.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientShiftPreference>
            {
                new() { ClientId = preferred, ShiftId = ShiftId, PreferenceType = ShiftPreferenceType.Preferred }
            });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        var candidates = DataAsJson(result).GetProperty("Candidates").EnumerateArray().ToList();
        candidates[0].GetProperty("ClientId").GetGuid().ShouldBe(preferred);
        candidates[0].GetProperty("IsPreferred").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task OnLeaveMember_IsExcludedAsAbsent()
    {
        var onLeave = Guid.NewGuid();
        var free = Guid.NewGuid();
        SetMembers(Member(onLeave, "Lena"), Member(free, "Cara"));
        SetConflicts();
        SetOnLeave(new ScheduleCell
        {
            ClientId = onLeave,
            EntryType = (int)ScheduleEntryType.Break,
            EntryDate = Date.ToDateTime(TimeOnly.MinValue)
        });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        var data = DataAsJson(result);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(1);
        data.GetProperty("Candidates").EnumerateArray().Single()
            .GetProperty("ClientId").GetGuid().ShouldBe(free);
        data.GetProperty("Excluded").EnumerateArray().Single()
            .GetProperty("Reason").GetString().ShouldBe("absent");
    }

    [Test]
    public async Task NoMembers_ReturnsEmpty()
    {
        SetMembers();
        SetConflicts();

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("EligibleCount").GetInt32().ShouldBe(0);
        result.Message.ShouldContain("No active members");
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
    public async Task InvalidAnalyseToken_ReturnsError()
    {
        SetMembers(Member(Guid.NewGuid(), "Anna"));
        var p = Params();
        p["analyseToken"] = "not-a-guid";

        var result = await Skill().ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("analyseToken");
    }
}
