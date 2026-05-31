// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the shift-lifecycle skills + delete handler: update_shift patches only the supplied
/// fields (times/weekdays survive when only the name is changed); for a cut parent it refuses
/// structural edits and propagates metadata-only changes across the whole cut group; delete_shift
/// refuses cuts (redirecting to the cut editor) and active works and otherwise soft-deletes; the new
/// Shift delete handler soft-deletes via the repository.
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
    public async Task Update_CutParent_StructuralEdit_Refuses()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(lft: 1, rgt: 6));
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["startTime"] = "09:00"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("cut editor");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_CutParent_MetadataOnly_PropagatesAcrossGroup()
    {
        var originalId = Guid.NewGuid();
        var parent = ExistingShift(lft: 1, rgt: 6);
        parent.OriginalId = originalId;

        var cutA = new Shift { Id = Guid.NewGuid(), Name = "DayShift", Abbreviation = "DAY", OriginalId = originalId, Status = ShiftStatus.SplitShift, StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(12, 0), Lft = 2, Rgt = 3 };
        var cutB = new Shift { Id = Guid.NewGuid(), Name = "DayShift", Abbreviation = "DAY", OriginalId = originalId, Status = ShiftStatus.SplitShift, StartShift = new TimeOnly(12, 0), EndShift = new TimeOnly(16, 0), Lft = 4, Rgt = 5 };

        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(parent);
        repo.CutList(originalId, Arg.Any<DateOnly?>(), Arg.Any<bool>())
            .Returns(new List<Shift> { cutA, cutB });

        var mediator = Substitute.For<IMediator>();
        var captured = new List<ShiftResource>();
        mediator.Send(Arg.Do<PutCommand<ShiftResource>>(c => captured.Add(c.Resource)), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ShiftResource>)ci[0]).Resource);

        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["name"] = "NightShift"
        });

        result.Success.ShouldBeTrue();
        // Parent + 2 cuts all renamed; cut times preserved (structure untouched).
        captured.Count.ShouldBe(3);
        captured.ShouldAllBe(r => r.Name == "NightShift");
        captured.ShouldContain(r => r.Id == cutA.Id && r.StartShift == new TimeOnly(8, 0));
        captured.ShouldContain(r => r.Id == cutB.Id && r.EndShift == new TimeOnly(16, 0));
        captured.ShouldContain(r => r.Id == ShiftId);
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
