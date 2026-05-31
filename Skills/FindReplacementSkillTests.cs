// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the thin find_replacement skill: it resolves the shift (errors when missing),
/// validates the analyseToken, dispatches FindReplacementQuery and projects the result. Candidate
/// selection / ranking logic is covered by FindReplacementQueryHandlerTests.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
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

    private FindReplacementSkill Skill() => new(_shiftRepo, _mediator);

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
    public async Task InvalidAnalyseToken_ReturnsError()
    {
        var p = Params();
        p["analyseToken"] = "not-a-guid";

        var result = await Skill().ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("analyseToken");
    }
}
