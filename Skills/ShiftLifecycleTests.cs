// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the shift-lifecycle skills + delete handler: update_shift patches only the supplied
/// fields (times/weekdays survive when only the name is changed) and refuses shifts with cuts;
/// delete_shift refuses cuts and active works and otherwise soft-deletes; the new Shift delete handler
/// soft-deletes via the repository.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.Logging;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ShiftLifecycleTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanDeleteShifts" }
    };

    private static Shift ExistingShift(int? lft = null, int? rgt = null) => new()
    {
        Id = ShiftId,
        Name = "DayShift",
        Abbreviation = "DAY",
        Status = ShiftStatus.OriginalShift,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
        IsSaturday = false, IsSunday = false,
        ShiftType = ShiftType.IsTask, Quantity = 1, SumEmployees = 1, WorkTime = 8m,
        Lft = lft, Rgt = rgt
    };

    [Test]
    public async Task Update_OnlyName_PreservesTimesAndWeekdays()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift());
        repo.HasActiveWorksAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(false);

        var mediator = Substitute.For<IMediator>();
        ShiftResource? captured = null;
        mediator.Send(Arg.Do<PutCommand<ShiftResource>>(c => captured = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ShiftResource>)ci[0]).Resource);

        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["name"] = "Renamed"
        });

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Name.ShouldBe("Renamed");
        captured.StartShift.ShouldBe(new TimeOnly(8, 0));
        captured.EndShift.ShouldBe(new TimeOnly(16, 0));
        captured.IsMonday.ShouldBeTrue();
        captured.IsFriday.ShouldBeTrue();
        captured.IsSunday.ShouldBeFalse();
    }

    [Test]
    public async Task Update_ShiftWithCuts_Refuses()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(lft: 1, rgt: 6));
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["name"] = "X"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("cuts");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ShiftWithCuts_Refuses()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(lft: 1, rgt: 4));
        var mediator = Substitute.For<IMediator>();
        var skill = new DeleteShiftSkill(repo, mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("cuts");
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ShiftWithActiveWorks_Refuses()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift());
        repo.HasActiveWorksAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(true);
        var mediator = Substitute.For<IMediator>();
        var skill = new DeleteShiftSkill(repo, mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("works");
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_Clean_Dispatches()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift());
        repo.HasActiveWorksAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(false);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ShiftResource { Id = ShiftId, Name = "DayShift" });
        var skill = new DeleteShiftSkill(repo, mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(Arg.Is<DeleteCommand<ShiftResource>>(c => c.Id == ShiftId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteHandler_SoftDeletes_ReturnsResource()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift());
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new DeleteCommandHandler(repo, new ScheduleMapper(), uow, Substitute.For<ILogger<DeleteCommandHandler>>());

        var result = await handler.Handle(new DeleteCommand<ShiftResource>(ShiftId), CancellationToken.None);

        result.ShouldNotBeNull();
        await repo.Received(1).Delete(ShiftId);
        await uow.Received(1).CompleteAsync();
    }
}
