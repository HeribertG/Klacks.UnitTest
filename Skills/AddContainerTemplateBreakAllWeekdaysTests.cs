// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// add_container_template_break with allWeekdays: the same break goes into every non-holiday weekday template under one
/// lock and one update of the complete list, a fixed break is skipped (and reported) where the last visit ends before it,
/// every changed template is verified by its own re-read, and the route is re-optimized per weekday. The write and the
/// verification run in one transaction: the fake unit of work restores the stored templates when its operation throws, so a
/// failed verification or a Put that fails on a later template leaves nothing behind. The mediator stands in for the
/// database: Put stores a copy of the list it receives, every Get hands out a fresh copy.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddContainerTemplateBreakAllWeekdaysTests
{
    private const int StorageMonday = 1;
    private const int StorageWednesday = 3;
    private const int StorageSunday = 0;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid VisitId = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private IMediator _mediator = null!;
    private IUserService _userService = null!;
    private IContainerRouteWriteService _routeWriteService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AddContainerTemplateBreakSkill _skill = null!;
    private List<ContainerTemplateResource> _stored = null!;
    private Func<ContainerTemplateResource, bool> _persists = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _mediator = Substitute.For<IMediator>();
        _userService = Substitute.For<IUserService>();
        _routeWriteService = Substitute.For<IContainerRouteWriteService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userService.GetInstanceId().Returns("tab-1");
        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, Name = "Spitex Nord", ShiftType = ShiftType.IsContainer });
        _absenceRepository.List().Returns(_ => new List<Absence>
        {
            new()
            {
                Id = SeededAbsenceIds.UnpaidBreak,
                Name = new MultiLanguage { De = "Pause unbezahlt", En = "Unpaid break", It = "Pausa non retribuita" },
                Abbreviation = new MultiLanguage(),
                Description = new MultiLanguage(),
                AppliesToContainer = true
            }
        });

        _stored =
        [
            Template(StorageMonday, Visit(new TimeOnly(8, 0), new TimeOnly(9, 0)), Visit(new TimeOnly(13, 0), new TimeOnly(14, 0))),
            Template(StorageWednesday, Visit(new TimeOnly(8, 0), new TimeOnly(9, 0)), Visit(new TimeOnly(13, 0), new TimeOnly(14, 0))),
            Template(StorageSunday, Visit(new TimeOnly(8, 0), new TimeOnly(9, 0)), Visit(new TimeOnly(13, 0), new TimeOnly(14, 0))),
        ];
        _stored[2].IsHoliday = true;
        _persists = _ => true;

        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => _stored.Select(Clone).ToList());
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var written = call.Arg<PutContainerTemplatesCommand>().Resources.Select(Clone).ToList();
                _stored = written.Select(template => _persists(template) ? template : StoredBefore(template.Id)).ToList();
                return written;
            });
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = true });
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>()).Returns(async call =>
        {
            var snapshot = _stored.Select(Clone).ToList();
            try
            {
                return await call.Arg<Func<Task<bool>>>()();
            }
            catch
            {
                _stored = snapshot;
                throw;
            }
        });

        _skill = new AddContainerTemplateBreakSkill(
            _shiftRepository, _absenceRepository, _mediator, _userService, ShiftVisibilityGuards.AllowAll(), _unitOfWork,
            _routeWriteService, NullLogger<AddContainerTemplateBreakSkill>.Instance);
    }

    [Test]
    public async Task AllWeekdays_AddsTheBreakToEveryWeekdayTemplate_InOneUpdate_AndLeavesTheHolidayTemplateAlone()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00", durationMinutes: 30));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>());
        BreakCount(_stored[0]).ShouldBe(1);
        BreakCount(_stored[1]).ShouldBe(1);
        BreakCount(_stored[2]).ShouldBe(0);
        _stored[0].ContainerTemplateItems.Select(i => i.ShiftId ?? i.AbsenceId)
            .ShouldBe(new Guid?[] { VisitId, SeededAbsenceIds.UnpaidBreak, VisitId });
        var weekdays = Json(result).GetProperty("Weekdays").EnumerateArray().ToList();
        weekdays.Select(w => w.GetProperty("Weekday").GetInt32()).ShouldBe(new[] { 1, 3 });
        weekdays.ShouldAllBe(w => w.GetProperty("Added").GetBoolean());
        Json(result).GetProperty("Verified").GetBoolean().ShouldBeTrue();
        result.Message.ShouldContain("Monday, Wednesday");
    }

    [Test]
    public async Task AllWeekdays_NeedsNoWeekday_AndIgnoresOneThatIsGiven()
    {
        var parameters = Params(startTime: "12:00");
        parameters["weekday"] = 5;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        BreakCount(_stored[0]).ShouldBe(1);
        BreakCount(_stored[1]).ShouldBe(1);
    }

    [Test]
    public async Task FixedBreakAfterTheLastVisit_IsSkippedAndReportedForThatWeekday()
    {
        _stored[1].ContainerTemplateItems = [Visit(new TimeOnly(8, 0), new TimeOnly(11, 30))];

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00"));

        result.Success.ShouldBeTrue(result.Message);
        BreakCount(_stored[0]).ShouldBe(1);
        BreakCount(_stored[1]).ShouldBe(0);
        var wednesday = Json(result).GetProperty("Weekdays").EnumerateArray().Single(w => w.GetProperty("Weekday").GetInt32() == 3);
        wednesday.GetProperty("Added").GetBoolean().ShouldBeFalse();
        wednesday.GetProperty("SkippedReason").GetString().ShouldContain("ends at 11:30, before the break starts at 12:00");
        result.Message.ShouldContain("Skipped: Wednesday (its last visit ends at 11:30");
    }

    [Test]
    public async Task MovableBreak_IsNeverSkipped()
    {
        _stored[1].ContainerTemplateItems = [Visit(new TimeOnly(8, 0), new TimeOnly(9, 0))];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        BreakCount(_stored[0]).ShouldBe(1);
        BreakCount(_stored[1]).ShouldBe(1);
    }

    [Test]
    public async Task EveryTemplateSkipped_IsAnError_AndWritesNothing()
    {
        _stored[0].ContainerTemplateItems = [Visit(new TimeOnly(8, 0), new TimeOnly(9, 0))];
        _stored[1].ContainerTemplateItems = [Visit(new TimeOnly(8, 0), new TimeOnly(9, 0))];

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("every weekday template was skipped");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyHolidayTemplates_IsAClearError()
    {
        _stored = [_stored[2]];

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no weekday template");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TemplateThatDidNotPersist_FailsVerificationNamingThatWeekdayOnly()
    {
        var wednesdayId = _stored[1].Id;
        _persists = template => template.Id != wednesdayId;

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Verification failed");
        result.Message.ShouldContain("Wednesday");
        result.Message.ShouldNotContain("Monday");
        result.Message.ShouldContain("rolled back");
        BreakCount(_stored[0]).ShouldBe(0);
        BreakCount(_stored[1]).ShouldBe(0);
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _routeWriteService.DidNotReceiveWithAnyArgs().OptimizeAndWriteUnderHeldLockAsync(default!, default, default, default, default);
    }

    [Test]
    public async Task PutThatFailsOnTheSecondTemplate_RollsTheFirstTemplateBack_AndPropagatesTheFailure()
    {
        var before = _stored.Select(Clone).ToList();
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns<List<ContainerTemplateResource>>(call =>
            {
                var written = call.Arg<PutContainerTemplatesCommand>().Resources.Select(Clone).ToList();
                _stored = [written[0], .._stored.Skip(1)];
                throw new InvalidOperationException("second template rejected");
            });

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00")));

        thrown.Message.ShouldBe("second template rejected");
        _stored.Select(BreakCount).ShouldBe(before.Select(BreakCount));
        _unitOfWork.Received(1).DiscardTrackedChanges();
        BreakCount(_stored[0]).ShouldBe(0);
        await _routeWriteService.DidNotReceiveWithAnyArgs().OptimizeAndWriteUnderHeldLockAsync(default!, default, default, default, default);
    }

    [Test]
    public async Task EqualBreakAlreadyStored_DoesNotPassVerificationByPresenceAlone()
    {
        _stored[0].ContainerTemplateItems.Add(new ContainerTemplateItemResource
        {
            Id = Guid.NewGuid(),
            AbsenceId = SeededAbsenceIds.UnpaidBreak,
            StartItem = new TimeOnly(12, 0),
            EndItem = new TimeOnly(12, 30)
        });
        var mondayId = _stored[0].Id;
        _persists = template => template.Id != mondayId;

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00", durationMinutes: 30));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Monday");
    }

    [Test]
    public async Task Reoptimize_RunsPerChangedWeekdayUnderTheSameLock_AndReportsAFailureForItsWeekdayOnly()
    {
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Is<ContainerTemplateLockScope>(scope => scope.Acquired), ContainerId, StorageMonday, false, Arg.Any<CancellationToken>())
            .Returns(new ContainerRouteWriteResult(null, "Base", "Base", 4.5, "02:10", false, [], 1));
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Is<ContainerTemplateLockScope>(scope => scope.Acquired), ContainerId, StorageWednesday, false, Arg.Any<CancellationToken>())
            .Returns(ContainerRouteWriteResult.Failure("At least 2 visits are needed."));

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00", reoptimizeRoute: true));

        result.Success.ShouldBeTrue(result.Message);
        var weekdays = Json(result).GetProperty("Weekdays").EnumerateArray().ToList();
        weekdays[0].GetProperty("RouteReoptimized").GetBoolean().ShouldBeTrue();
        weekdays[1].GetProperty("RouteReoptimized").GetBoolean().ShouldBeFalse();
        weekdays[1].GetProperty("RouteError").GetString().ShouldBe("At least 2 visits are needed.");
        result.Message.ShouldContain("re-optimized for 1 of 2 weekday(s)");
        result.Message.ShouldContain("Wednesday (At least 2 visits are needed.)");
        BreakCount(_stored[1]).ShouldBe(1);
        await _routeWriteService.Received(2).OptimizeAndWriteUnderHeldLockAsync(
            Arg.Any<ContainerTemplateLockScope>(), ContainerId, Arg.Any<int>(), false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThrowingReoptimize_IsReportedForItsWeekday_AndKeepsTheBreak()
    {
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Any<ContainerTemplateLockScope>(), ContainerId, StorageMonday, false, Arg.Any<CancellationToken>())
            .Returns<Task<ContainerRouteWriteResult>>(_ => throw new InvalidOperationException("routing down"));
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Any<ContainerTemplateLockScope>(), ContainerId, StorageWednesday, false, Arg.Any<CancellationToken>())
            .Returns(new ContainerRouteWriteResult(null, "Base", "Base", 1, "01:00", false, [], 1));

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00", reoptimizeRoute: true));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldNotContain("routing down");
        BreakCount(_stored[0]).ShouldBe(1);
        Json(result).GetProperty("Weekdays")[0].GetProperty("RouteReoptimized").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task LockHeldByAnotherUser_WritesNothing()
    {
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = false });

        var result = await _skill.ExecuteAsync(Ctx(), Params(startTime: "12:00"));

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutAllWeekdays_TheWeekdayStaysRequired()
    {
        var parameters = new Dictionary<string, object> { ["containerId"] = ContainerId.ToString() };

        var exception = await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), parameters));

        exception.Message.ShouldContain("weekday");
    }

    [Test]
    public async Task AllWeekdaysFalse_BehavesLikeTheSingleWeekdayMode()
    {
        var parameters = Params(startTime: "12:00");
        parameters["allWeekdays"] = false;
        parameters["weekday"] = 1;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        BreakCount(_stored[0]).ShouldBe(1);
        BreakCount(_stored[1]).ShouldBe(0);
    }

    private ContainerTemplateResource StoredBefore(Guid templateId) =>
        _stored.Single(template => template.Id == templateId);

    private static int BreakCount(ContainerTemplateResource template) =>
        template.ContainerTemplateItems.Count(i => i.AbsenceId == SeededAbsenceIds.UnpaidBreak);

    private static ContainerTemplateResource Template(int storageWeekday, params ContainerTemplateItemResource[] items) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = ContainerId,
        Weekday = storageWeekday,
        FromTime = new TimeOnly(8, 0),
        UntilTime = new TimeOnly(17, 0),
        ContainerTemplateItems = items.ToList()
    };

    private static ContainerTemplateItemResource Visit(TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = VisitId,
        StartItem = start,
        EndItem = end
    };

    private static ContainerTemplateResource Clone(ContainerTemplateResource template) => new()
    {
        Id = template.Id,
        ContainerId = template.ContainerId,
        Weekday = template.Weekday,
        IsHoliday = template.IsHoliday,
        IsWeekdayAndHoliday = template.IsWeekdayAndHoliday,
        FromTime = template.FromTime,
        UntilTime = template.UntilTime,
        ContainerTemplateItems = template.ContainerTemplateItems.Select(i => new ContainerTemplateItemResource
        {
            Id = i.Id,
            ShiftId = i.ShiftId,
            AbsenceId = i.AbsenceId,
            StartItem = i.StartItem,
            EndItem = i.EndItem,
            BriefingTime = i.BriefingTime,
            DebriefingTime = i.DebriefingTime,
            TravelTimeBefore = i.TravelTimeBefore,
            TravelTimeAfter = i.TravelTimeAfter,
            TimeRangeStartItem = i.TimeRangeStartItem,
            TimeRangeEndItem = i.TimeRangeEndItem,
            TransportMode = i.TransportMode
        }).ToList()
    };

    private static JsonElement Json(SkillResult result) => JsonSerializer.SerializeToElement(result.Data);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserLanguage = "de",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(
        string? startTime = null, int? durationMinutes = null, bool? reoptimizeRoute = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["containerId"] = ContainerId.ToString(),
            ["allWeekdays"] = true
        };
        if (startTime is not null)
        {
            parameters["startTime"] = startTime;
        }

        if (durationMinutes.HasValue)
        {
            parameters["durationMinutes"] = durationMinutes.Value;
        }

        if (reoptimizeRoute.HasValue)
        {
            parameters["reoptimizeRoute"] = reoptimizeRoute.Value;
        }

        return parameters;
    }
}
