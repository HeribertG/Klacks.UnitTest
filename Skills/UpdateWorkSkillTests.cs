// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateWorkSkill — lock-level refusal, partial time update with work-time
/// recomputation, no-op, verified happy path and the stale-reread failure path — and for
/// GetPeriodHoursSkill — balance reporting, invalid range and missing-client handling.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.PeriodHours;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateWorkSkillTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewShifts" }
    };

    private static WorkResource Work(Guid id, string start = "08:00", string end = "17:00",
        decimal workTime = 9m, WorkLockLevel lockLevel = WorkLockLevel.None) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        ShiftId = Guid.NewGuid(),
        CurrentDate = new DateOnly(2026, 7, 15),
        StartTime = TimeOnly.Parse(start),
        EndTime = TimeOnly.Parse(end),
        WorkTime = workTime,
        LockLevel = lockLevel
    };

    [Test]
    public async Task UpdatesTimes_RecomputesWorkTime_AndReportsVerified()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetQuery<WorkResource>>(), Arg.Any<CancellationToken>())
            .Returns(
                Work(id),
                Work(id, start: "09:00", end: "18:30", workTime: 9.5m));
        _mediator.Send(Arg.Any<PutCommand<WorkResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<PutCommand<WorkResource>>().Resource);
        var skill = new UpdateWorkSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = id.ToString(),
            ["startTime"] = "09:00",
            ["endTime"] = "18:30"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<PutCommand<WorkResource>>(c =>
                c.Resource.StartTime == TimeOnly.Parse("09:00")
                && c.Resource.EndTime == TimeOnly.Parse("18:30")
                && c.Resource.WorkTime == 9.5m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefusesLockedWork_NamingTheLockLevel()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetQuery<WorkResource>>(), Arg.Any<CancellationToken>())
            .Returns(Work(id, lockLevel: WorkLockLevel.Confirmed));
        var skill = new UpdateWorkSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = id.ToString(),
            ["startTime"] = "09:00"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Confirmed");
        result.Message.ShouldContain("unconfirm_work");
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<WorkResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoOp_WhenNothingSupplied()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetQuery<WorkResource>>(), Arg.Any<CancellationToken>())
            .Returns(Work(id));
        var skill = new UpdateWorkSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("No fields");
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<WorkResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_OnInvalidTimeFormat()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetQuery<WorkResource>>(), Arg.Any<CancellationToken>())
            .Returns(Work(id));
        var skill = new UpdateWorkSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = id.ToString(),
            ["startTime"] = "quarter past nine"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid startTime");
    }

    [Test]
    public async Task ReturnsError_WhenVerificationShowsOldTimes()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetQuery<WorkResource>>(), Arg.Any<CancellationToken>())
            .Returns(Work(id), Work(id));
        _mediator.Send(Arg.Any<PutCommand<WorkResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<PutCommand<WorkResource>>().Resource);
        var skill = new UpdateWorkSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = id.ToString(),
            ["startTime"] = "09:00"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }
}

[TestFixture]
public class GetPeriodHoursSkillTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    [Test]
    public async Task ReportsBalance_ForTheRequestedClient()
    {
        var clientId = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetPeriodHoursQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>
            {
                [clientId] = new() { Hours = 152m, Surcharges = 4.5m, GuaranteedHours = 160m }
            });
        var skill = new GetPeriodHoursSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = clientId.ToString(),
            ["startDate"] = "2026-07-01",
            ["endDate"] = "2026-07-31"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("152");
        result.Message.ShouldContain("160");
        result.Message.ShouldContain("-8");
        await _mediator.Received(1).Send(
            Arg.Is<GetPeriodHoursQuery>(q =>
                q.Request.ClientIds.Single() == clientId
                && q.Request.StartDate == new DateOnly(2026, 7, 1)
                && q.Request.EndDate == new DateOnly(2026, 7, 31)
                && q.Request.AnalyseToken == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenEndBeforeStart()
    {
        var skill = new GetPeriodHoursSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["startDate"] = "2026-07-31",
            ["endDate"] = "2026-07-01"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must not be before");
        await _mediator.DidNotReceive().Send(Arg.Any<GetPeriodHoursQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenClientHasNoPeriodHours()
    {
        _mediator.Send(Arg.Any<GetPeriodHoursQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>());
        var skill = new GetPeriodHoursSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["startDate"] = "2026-07-01",
            ["endDate"] = "2026-07-31"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No period hours found");
    }
}
