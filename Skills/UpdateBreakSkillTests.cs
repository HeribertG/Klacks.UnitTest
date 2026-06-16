// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateBreakSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static BreakResource Break(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        AbsenceId = Guid.NewGuid(),
        CurrentDate = new DateOnly(2026, 1, 1),
        StartTime = new TimeOnly(0, 0),
        EndTime = new TimeOnly(23, 59),
        WorkTime = 8m
    };

    [Test]
    public async Task UpdateWorkTimeAndTimes_DispatchesPutCommand()
    {
        var breakId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<BreakResource>>(), Arg.Any<CancellationToken>())
            .Returns(Break(breakId));
        mediator.Send(Arg.Any<PutCommand<BreakResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<BreakResource>)ci[0]).Resource);
        var skill = new UpdateBreakSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["breakId"] = breakId.ToString(),
            ["workTime"] = 4m,
            ["startTime"] = "08:00",
            ["endTime"] = "12:00"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<BreakResource>>(c =>
                c.Resource.Id == breakId &&
                c.Resource.WorkTime == 4m &&
                c.Resource.StartTime == new TimeOnly(8, 0) &&
                c.Resource.EndTime == new TimeOnly(12, 0)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownBreak_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<BreakResource>>(), Arg.Any<CancellationToken>())
            .Returns<BreakResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateBreakSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["breakId"] = Guid.NewGuid().ToString(),
            ["workTime"] = 4m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<BreakResource>>(), Arg.Any<CancellationToken>());
    }
}
