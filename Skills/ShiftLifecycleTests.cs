// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the shift-lifecycle skills + delete handler: update_shift patches only the supplied
/// fields (times/weekdays survive when only the name is changed) and writes exactly one shift through the handler, which
/// refuses locked content; delete_shift refuses cuts (redirecting to the cut editor) and active works, lets an administrator
/// delete a whole sealed order family and leaves every other non-draft shift to the handler; the shift delete handler
/// soft-deletes a draft via the repository and applies the same rules.
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using System.Security.Claims;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Http;
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

    private static Shift ExistingShift(
        int? lft = null,
        int? rgt = null,
        ShiftStatus status = ShiftStatus.OriginalShift) => new()
    {
        Id = ShiftId,
        Name = "DayShift",
        Abbreviation = "DAY",
        Status = status,
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

        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator, ShiftVisibilityGuards.AllowAll());

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
    public async Task Update_LockedShift_RefusalOfTheHandlerReachesTheCaller()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(lft: 1, rgt: 6));
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PutCommand<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns<Task<ShiftResource?>>(_ => throw ShiftLockedException.ForEdit(
                ExistingShift(lft: 1, rgt: 6), [nameof(Shift.StartShift)]));
        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator, ShiftVisibilityGuards.AllowAll());

        var thrown = await Should.ThrowAsync<ShiftLockedException>(() => skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["startTime"] = "09:00"
        }));

        thrown.ConflictCode.ShouldBe(ShiftLockedException.ErrorCode);
        thrown.Message.ShouldContain(nameof(Shift.StartShift));
        thrown.Message.ShouldContain("cut editor");
    }

    [Test]
    public async Task Update_CutParent_IsOneWriteThroughTheHandler_NeverAPropagationAcrossTheGroup()
    {
        var originalId = Guid.NewGuid();
        var parent = ExistingShift(lft: 1, rgt: 6);
        parent.OriginalId = originalId;
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(parent);
        var mediator = Substitute.For<IMediator>();
        var captured = new List<ShiftResource>();
        mediator.Send(Arg.Do<PutCommand<ShiftResource>>(c => captured.Add(c.Resource)), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ShiftResource>)ci[0]).Resource);
        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator, ShiftVisibilityGuards.AllowAll());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["name"] = "Renamed"
        });

        result.Success.ShouldBeTrue("the handler, not the skill, decides whether the rename of a locked shift is refused");
        captured.Count.ShouldBe(1);
        captured[0].Id.ShouldBe(ShiftId);
        await repo.DidNotReceiveWithAnyArgs().CutList(default, default, default);
    }

    [Test]
    public async Task Update_CutParent_WithAnEndDate_IsRefusedAndPointsToTheCutEditorReset()
    {
        var parent = ExistingShift(lft: 1, rgt: 6);
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(parent);
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator, ShiftVisibilityGuards.AllowAll());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["untilDate"] = "2026-12-31"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Reset");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_ShiftWithoutCuts_CanStillGetAFirstEndDate()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift());
        var mediator = Substitute.For<IMediator>();
        ShiftResource? captured = null;
        mediator.Send(Arg.Do<PutCommand<ShiftResource>>(c => captured = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ShiftResource>)ci[0]).Resource);
        var skill = new UpdateShiftSkill(repo, new ScheduleMapper(), mediator, ShiftVisibilityGuards.AllowAll());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["untilDate"] = "2026-12-31"
        });

        result.Success.ShouldBeTrue();
        captured!.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
    }

    [Test]
    public async Task Delete_ShiftWithCuts_Refuses()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(lft: 1, rgt: 4));
        var mediator = Substitute.For<IMediator>();
        var skill = new DeleteShiftSkill(repo, mediator, Substitute.For<ISealedOrderFamilyDeletion>(), ShiftVisibilityGuards.AllowAll());

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
        var skill = new DeleteShiftSkill(repo, mediator, Substitute.For<ISealedOrderFamilyDeletion>(), ShiftVisibilityGuards.AllowAll());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("works");
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    private static SkillExecutionContext AdminCtx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin, "CanEditShifts", "CanDeleteShifts" }
    };

    [TestCase(ShiftStatus.SealedOrder)]
    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task Delete_NonDraftShift_BySupervisor_IsLeftToTheHandlerWhichRefusesIt(ShiftStatus status)
    {
        var shift = ExistingShift(status: status);
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(shift);
        repo.HasActiveWorksAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(false);
        var familyDeletion = Substitute.For<ISealedOrderFamilyDeletion>();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns<Task<ShiftResource?>>(_ => throw ShiftLockedException.ForDelete(shift));
        var skill = new DeleteShiftSkill(repo, mediator, familyDeletion, ShiftVisibilityGuards.AllowAll());

        var thrown = await Should.ThrowAsync<ShiftLockedException>(() =>
            skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() }));

        thrown.ConflictCode.ShouldBe(ShiftLockedException.ErrorCode);
        thrown.Message.ShouldContain(status.ToString());
        await familyDeletion.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Test]
    public async Task Delete_SealedOrder_ByAdministrator_DeletesTheWholeFamilyWithoutTheHandler()
    {
        var order = ExistingShift(status: ShiftStatus.SealedOrder);
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(order);
        var familyDeletion = Substitute.For<ISealedOrderFamilyDeletion>();
        familyDeletion.DeleteAsync(order, Arg.Any<CancellationToken>()).Returns(4);
        var mediator = Substitute.For<IMediator>();
        var skill = new DeleteShiftSkill(repo, mediator, familyDeletion, ShiftVisibilityGuards.AllowAll());

        var result = await skill.ExecuteAsync(AdminCtx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("3 piece");
        await familyDeletion.Received(1).DeleteAsync(order, Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_SealedOrder_ByAdministrator_FamilyStillInUse_TheRefusalReachesTheCaller()
    {
        var order = ExistingShift(status: ShiftStatus.SealedOrder);
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(order);
        var familyDeletion = Substitute.For<ISealedOrderFamilyDeletion>();
        familyDeletion.DeleteAsync(order, Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw ShiftLockedException.ForOrderFamilyInUse(
                order, new OrderFamilyUsage([ShiftId], HasWorks: true, HasScenarioClones: false, HasContainerReferences: false)));
        var skill = new DeleteShiftSkill(repo, Substitute.For<IMediator>(), familyDeletion, ShiftVisibilityGuards.AllowAll());

        var thrown = await Should.ThrowAsync<ShiftLockedException>(() =>
            skill.ExecuteAsync(AdminCtx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() }));

        thrown.Message.ShouldContain("works exist");
    }

    [Test]
    public async Task Delete_PlannableShift_ByAdministrator_IsNotAFamilyDelete()
    {
        var shift = ExistingShift(status: ShiftStatus.OriginalShift);
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(shift);
        var familyDeletion = Substitute.For<ISealedOrderFamilyDeletion>();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns<Task<ShiftResource?>>(_ => throw ShiftLockedException.ForDelete(shift));
        var skill = new DeleteShiftSkill(repo, mediator, familyDeletion, ShiftVisibilityGuards.AllowAll());

        await Should.ThrowAsync<ShiftLockedException>(() =>
            skill.ExecuteAsync(AdminCtx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() }));

        await familyDeletion.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Test]
    public async Task Delete_Clean_Dispatches()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(status: ShiftStatus.OriginalOrder));
        repo.HasActiveWorksAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(false);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ShiftResource { Id = ShiftId, Name = "DayShift" });
        var skill = new DeleteShiftSkill(repo, mediator, Substitute.For<ISealedOrderFamilyDeletion>(), ShiftVisibilityGuards.AllowAll());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString() });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(Arg.Is<DeleteCommand<ShiftResource>>(c => c.Id == ShiftId), Arg.Any<CancellationToken>());
    }

    private static DeleteCommandHandler HandlerFor(
        IShiftRepository repo, ISealedOrderFamilyDeletion familyDeletion, string? role, IUnitOfWork? uow = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        if (role != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        }

        accessor.HttpContext.Returns(context);
        return new DeleteCommandHandler(
            repo, new ScheduleMapper(), uow ?? Substitute.For<IUnitOfWork>(), familyDeletion, accessor, ShiftVisibilityGuards.AllowAll(),
            Substitute.For<ILogger<DeleteCommandHandler>>());
    }

    [Test]
    public async Task DeleteHandler_SoftDeletes_ReturnsResource()
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(status: ShiftStatus.OriginalOrder));
        var uow = Substitute.For<IUnitOfWork>();
        var handler = HandlerFor(repo, Substitute.For<ISealedOrderFamilyDeletion>(), role: null, uow);

        var result = await handler.Handle(new DeleteCommand<ShiftResource>(ShiftId), CancellationToken.None);

        result.ShouldNotBeNull();
        await repo.Received(1).Delete(ShiftId);
        await uow.Received(1).CompleteAsync();
    }

    [Test]
    public async Task DeleteHandler_SealedOrder_ByAdministrator_DeletesTheFamily_NotJustTheRow()
    {
        var order = ExistingShift(status: ShiftStatus.SealedOrder);
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(order);
        var familyDeletion = Substitute.For<ISealedOrderFamilyDeletion>();
        var handler = HandlerFor(repo, familyDeletion, Roles.Admin);

        var result = await handler.Handle(new DeleteCommand<ShiftResource>(ShiftId), CancellationToken.None);

        result.ShouldNotBeNull();
        await familyDeletion.Received(1).DeleteAsync(order, Arg.Any<CancellationToken>());
        await repo.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [TestCase(Roles.Authorised)]
    [TestCase(null)]
    public async Task DeleteHandler_SealedOrder_ByAnyoneButTheAdministrator_IsRefused(string? role)
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(status: ShiftStatus.SealedOrder));
        var familyDeletion = Substitute.For<ISealedOrderFamilyDeletion>();
        var handler = HandlerFor(repo, familyDeletion, role);

        await Should.ThrowAsync<ShiftLockedException>(() =>
            handler.Handle(new DeleteCommand<ShiftResource>(ShiftId), CancellationToken.None));

        await familyDeletion.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        await repo.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task DeleteHandler_PlannableShiftOrCutPiece_IsRefusedEvenForTheAdministrator(ShiftStatus status)
    {
        var repo = Substitute.For<IShiftRepository>();
        repo.Get(ShiftId).Returns(ExistingShift(status: status));
        var familyDeletion = Substitute.For<ISealedOrderFamilyDeletion>();
        var handler = HandlerFor(repo, familyDeletion, Roles.Admin);

        await Should.ThrowAsync<ShiftLockedException>(() =>
            handler.Handle(new DeleteCommand<ShiftResource>(ShiftId), CancellationToken.None));

        await familyDeletion.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }
}
