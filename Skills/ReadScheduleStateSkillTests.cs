// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the read_schedule_state skill: parameter validation, scenario-token pass-through,
/// projection of the schedule grid, and truncation. The Data payload is asserted via its serialized
/// JSON shape because the projection uses internal anonymous types (that JSON is the real LLM contract).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ReadScheduleStateSkillTests
{
    private const int MaxEntries = 750;
    private static readonly Guid GroupId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static ScheduleCell Cell(
        int entryType,
        DateTime date,
        int lockLevel = 0,
        bool groupRestricted = false,
        Guid? clientId = null) => new()
        {
            Id = Guid.NewGuid(),
            EntryType = entryType,
            ClientId = clientId ?? Guid.NewGuid(),
            EntryDate = date,
            StartTime = new TimeSpan(8, 0, 0),
            EndTime = new TimeSpan(16, 0, 0),
            EntryName = "Day shift",
            Abbreviation = "DS",
            LockLevel = lockLevel,
            IsGroupRestricted = groupRestricted
        };

    private static IScheduleEntriesService ServiceReturning(IEnumerable<ScheduleCell> cells)
    {
        var svc = Substitute.For<IScheduleEntriesService>();
        svc.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(cells));
        return svc;
    }

    private static Dictionary<string, object> Params(string? analyseToken = null)
    {
        var p = new Dictionary<string, object>
        {
            ["groupId"] = GroupId.ToString(),
            ["fromDate"] = "2026-03-02",
            ["untilDate"] = "2026-03-08"
        };
        if (analyseToken != null)
        {
            p["analyseToken"] = analyseToken;
        }
        return p;
    }

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task ValidGroupPeriod_ReturnsEntries()
    {
        var clientA = Guid.NewGuid();
        var cells = new List<ScheduleCell>
        {
            Cell((int)Klacks.Api.Domain.Enums.ScheduleEntryType.Work, new DateTime(2026, 3, 2), clientId: clientA),
            Cell((int)Klacks.Api.Domain.Enums.ScheduleEntryType.Break, new DateTime(2026, 3, 3), clientId: clientA)
        };
        var skill = new ReadScheduleStateSkill(ServiceReturning(cells));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("EntryCount").GetInt32().ShouldBe(2);
        data.GetProperty("DistinctEmployees").GetInt32().ShouldBe(1);
        data.GetProperty("IsScenario").GetBoolean().ShouldBeFalse();
        data.GetProperty("Truncated").GetBoolean().ShouldBeFalse();
        data.GetProperty("Entries").GetArrayLength().ShouldBe(2);
    }

    [Test]
    public async Task Projection_MapsEntryTypeLockLevelAndFlags()
    {
        var cells = new List<ScheduleCell>
        {
            Cell((int)Klacks.Api.Domain.Enums.ScheduleEntryType.Work, new DateTime(2026, 3, 2),
                lockLevel: (int)Klacks.Api.Domain.Enums.WorkLockLevel.Confirmed, groupRestricted: true)
        };
        var skill = new ReadScheduleStateSkill(ServiceReturning(cells));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        var entry = DataAsJson(result).GetProperty("Entries")[0];
        entry.GetProperty("EntryType").GetString().ShouldBe("Work");
        entry.GetProperty("LockLevel").GetString().ShouldBe("Confirmed");
        entry.GetProperty("IsLocked").GetBoolean().ShouldBeTrue();
        entry.GetProperty("IsGroupRestricted").GetBoolean().ShouldBeTrue();
        entry.GetProperty("Date").GetString().ShouldBe("2026-03-02");
    }

    [Test]
    public async Task ScenarioToken_PassedThroughToService()
    {
        var token = Guid.NewGuid();
        var svc = ServiceReturning(new List<ScheduleCell>());
        var skill = new ReadScheduleStateSkill(svc);

        var result = await skill.ExecuteAsync(Ctx(), Params(token.ToString()));

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("IsScenario").GetBoolean().ShouldBeTrue();
        svc.Received(1).GetScheduleEntriesQuery(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), token);
    }

    [Test]
    public async Task RealMode_PassesNullTokenToService()
    {
        var svc = ServiceReturning(new List<ScheduleCell>());
        var skill = new ReadScheduleStateSkill(svc);

        await skill.ExecuteAsync(Ctx(), Params());

        svc.Received(1).GetScheduleEntriesQuery(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), (Guid?)null);
    }

    [Test]
    public async Task InvalidFromDate_ReturnsError()
    {
        var skill = new ReadScheduleStateSkill(ServiceReturning(new List<ScheduleCell>()));
        var p = Params();
        p["fromDate"] = "not-a-date";

        var result = await skill.ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("fromDate");
    }

    [Test]
    public async Task UntilBeforeFrom_ReturnsError()
    {
        var skill = new ReadScheduleStateSkill(ServiceReturning(new List<ScheduleCell>()));
        var p = Params();
        p["untilDate"] = "2026-03-01";

        var result = await skill.ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task InvalidAnalyseToken_ReturnsError()
    {
        var skill = new ReadScheduleStateSkill(ServiceReturning(new List<ScheduleCell>()));

        var result = await skill.ExecuteAsync(Ctx(), Params("not-a-guid"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("analyseToken");
    }

    [Test]
    public async Task ExceedingCap_TruncatesResult()
    {
        var cells = Enumerable.Range(0, MaxEntries + 5)
            .Select(_ => Cell((int)Klacks.Api.Domain.Enums.ScheduleEntryType.Work, new DateTime(2026, 3, 2)))
            .ToList();
        var skill = new ReadScheduleStateSkill(ServiceReturning(cells));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        var data = DataAsJson(result);
        data.GetProperty("Truncated").GetBoolean().ShouldBeTrue();
        data.GetProperty("EntryCount").GetInt32().ShouldBe(MaxEntries);
    }
}
