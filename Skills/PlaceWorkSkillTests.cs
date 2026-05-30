// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the place_work pre-commit guardrail: a placement that would introduce a blocking
/// (Error) conflict must NOT be committed, while a clean placement commits via BulkAddWorksCommand.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PlaceWorkSkillTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["shiftId"] = ShiftId.ToString(),
        ["date"] = new DateOnly(2026, 3, 10)
    };

    private static (PlaceWorkSkill skill, IMediator mediator, IPreCommitConflictChecker checker) Build(
        PreCommitCheckResult checkResult)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse());

        var shiftRepo = Substitute.For<IShiftRepository>();
        shiftRepo.Get(ShiftId).Returns(new Shift
        {
            Id = ShiftId,
            Name = "Day shift",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0)
        });

        var checker = Substitute.For<IPreCommitConflictChecker>();
        checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(checkResult);

        return (new PlaceWorkSkill(mediator, shiftRepo, checker), mediator, checker);
    }

    [Test]
    public async Task BlockingConflict_ReturnsErrorAndDoesNotCommit()
    {
        var blocking = new PreCommitCheckResult(new List<ScheduleValidationNotificationDto>
        {
            new()
            {
                Type = ScheduleValidationType.Error,
                ClientId = ClientId,
                Date = new DateOnly(2026, 3, 10),
                Comment = "schedule.error-list.collision"
            }
        });
        var (skill, mediator, _) = Build(blocking);

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("blocked");
        await mediator.DidNotReceive().Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CleanPlacement_Commits()
    {
        var (skill, mediator, _) = Build(PreCommitCheckResult.Empty);

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WarningOnlyConflict_StillCommits()
    {
        var warningOnly = new PreCommitCheckResult(new List<ScheduleValidationNotificationDto>
        {
            new()
            {
                Type = ScheduleValidationType.Warning,
                ClientId = ClientId,
                Date = new DateOnly(2026, 3, 10),
                Comment = "schedule.error-list.rest-violation"
            }
        });
        var (skill, mediator, _) = Build(warningOnly);

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }
}
