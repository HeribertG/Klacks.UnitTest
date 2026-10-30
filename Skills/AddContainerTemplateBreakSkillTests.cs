// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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

/// <summary>
/// add_container_template_break: builds the fixed and the movable break item the way the container page stores breaks,
/// writes the full template list under the lock, verifies on a fresh re-read that exactly one equal break was added,
/// never releases a lock it reused, and re-optimizes the route under the same lock only on request. The mediator
/// stands in for the database: Put stores a copy, every Get hands out a fresh copy.
/// </summary>
[TestFixture]
public class AddContainerTemplateBreakSkillTests
{
    private const int StorageThursday = 4;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid VisitMorning = Guid.NewGuid();
    private static readonly Guid VisitAfternoon = Guid.NewGuid();
    private static readonly Guid PaidBreakId = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private IMediator _mediator = null!;
    private IUserService _userService = null!;
    private IContainerRouteWriteService _routeWriteService = null!;
    private AddContainerTemplateBreakSkill _skill = null!;
    private List<ContainerTemplateResource> _stored = null!;
    private bool _putPersists;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _mediator = Substitute.For<IMediator>();
        _userService = Substitute.For<IUserService>();
        _routeWriteService = Substitute.For<IContainerRouteWriteService>();
        _userService.GetInstanceId().Returns("tab-1");
        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, Name = "Spitex Nord", ShiftType = ShiftType.IsContainer });
        _absenceRepository.List().Returns(_ => Absences());

        _stored = [Template(Visit(VisitMorning, new TimeOnly(8, 0)), Visit(VisitAfternoon, new TimeOnly(13, 0)))];
        _putPersists = true;
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => _stored.Select(Clone).ToList());
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var written = call.Arg<PutContainerTemplatesCommand>().Resources.Select(Clone).ToList();
                if (_putPersists)
                {
                    _stored = written;
                }

                return written;
            });
        LockIs(alreadyHeld: false);
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        _skill = new AddContainerTemplateBreakSkill(
            _shiftRepository, _absenceRepository, _mediator, _userService, ShiftVisibilityGuards.AllowAll(),
            Substitute.For<IUnitOfWork>(), _routeWriteService, NullLogger<AddContainerTemplateBreakSkill>.Instance);
    }

    [Test]
    public async Task StartTime_AddsAFixedBreakBetweenTheVisits_AndLeavesTheRouteAlone()
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00", durationMinutes: 45));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        var items = _stored[0].ContainerTemplateItems;
        items.Select(i => i.ShiftId ?? i.AbsenceId).ShouldBe(new Guid?[] { VisitMorning, SeededAbsenceIds.UnpaidBreak, VisitAfternoon });
        var breakItem = items[1];
        breakItem.ShiftId.ShouldBeNull();
        breakItem.StartItem.ShouldBe(new TimeOnly(12, 0));
        breakItem.EndItem.ShouldBe(new TimeOnly(12, 45));
        breakItem.TimeRangeStartItem.ShouldBeNull();
        breakItem.TimeRangeEndItem.ShouldBeNull();
        breakItem.BriefingTime.ShouldBe(TimeOnly.MinValue);
        breakItem.DebriefingTime.ShouldBe(TimeOnly.MinValue);
        breakItem.TravelTimeBefore.ShouldBe(TimeOnly.MinValue);
        breakItem.TravelTimeAfter.ShouldBe(TimeOnly.MinValue);
        result.Message.ShouldContain("12:00-12:45");
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("may overlap the break until the route is re-optimized");
        await _routeWriteService.DidNotReceiveWithAnyArgs().OptimizeAndWriteUnderHeldLockAsync(default!, default, default, default, default);
        await _routeWriteService.DidNotReceiveWithAnyArgs().OptimizeAndWriteAsync(default, default, default, default!, default);
    }

    [Test]
    public async Task NoStartTime_AppendsAMovableBreakOfTheDefaultDuration()
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        var breakItem = _stored[0].ContainerTemplateItems.Last();
        breakItem.AbsenceId.ShouldBe(SeededAbsenceIds.UnpaidBreak);
        breakItem.StartItem.ShouldBeNull();
        breakItem.EndItem.ShouldBeNull();
        breakItem.TimeRangeStartItem.ShouldBe(new TimeOnly(0, 0));
        breakItem.TimeRangeEndItem.ShouldBe(new TimeOnly(0, ContainerVisitDefaults.DefaultMovableBlockMinutes));
        result.Message.ShouldContain("movable break");
    }

    [Test]
    public async Task IsoSunday_EditsTheStorageSundayTemplate()
    {
        // Arrange
        _stored = [Template(0)];

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(7, startTime: "12:00"));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        _stored[0].ContainerTemplateItems.ShouldHaveSingleItem().AbsenceId.ShouldBe(SeededAbsenceIds.UnpaidBreak);
    }

    [Test]
    public async Task AbsenceName_InAnyLanguage_SelectsThatContainerAbsence()
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00", absenceName: "pausa retribuita"));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        _stored[0].ContainerTemplateItems.Single(i => i.AbsenceId.HasValue).AbsenceId.ShouldBe(PaidBreakId);
    }

    [Test]
    public async Task AmbiguousAbsenceName_IsRefusedBeforeTakingTheLock()
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, absenceName: "Pause"));

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Several absence types");
        result.Message.ShouldContain("Paid break");
        result.Message.ShouldContain("Unpaid break");
        await _mediator.DidNotReceive().Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidStartTime_IsRefusedInsteadOfAddingAMovableBreak()
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "lunch"));

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("startTime");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [TestCase(0)]
    [TestCase(1440)]
    public async Task DurationOutOfRange_IsRefused(int durationMinutes)
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, durationMinutes: durationMinutes));

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("durationMinutes");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReusedLockOfTheOpenPage_IsNotReleased()
    {
        // Arrange
        LockIs(alreadyHeld: true);

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00"));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        await _mediator.DidNotReceive().Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OwnLock_IsReleasedAfterTheWrite()
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00"));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WriteThatDidNotPersist_FailsVerification()
    {
        // Arrange
        _putPersists = false;

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00"));

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Verification failed");
    }

    [Test]
    public async Task EqualBreakAlreadyStored_DoesNotPassVerificationByPresenceAlone()
    {
        // Arrange
        _stored[0].ContainerTemplateItems.Add(new ContainerTemplateItemResource
        {
            Id = Guid.NewGuid(),
            AbsenceId = SeededAbsenceIds.UnpaidBreak,
            StartItem = new TimeOnly(12, 0),
            EndItem = new TimeOnly(12, 30)
        });
        _putPersists = false;

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00"));

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Verification failed");
    }

    [Test]
    public async Task LockHeldByAnotherUser_WritesNothing()
    {
        // Arrange
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = false });

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00"));

        // Assert
        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reoptimize_RunsTheRouteWriteUnderTheSameLockBeforeReleasingIt()
    {
        // Arrange
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
            Arg.Is<ContainerTemplateLockScope>(scope => scope.Acquired), ContainerId, StorageThursday, false, Arg.Any<CancellationToken>())
            .Returns(new ContainerRouteWriteResult(
                null, "Base", "Base", 4.25, "02:10", false,
                [new ContainerRouteStop(1, VisitMorning, "Client", "Street 1", "08:15", "09:00", "00:10")], 1));

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00", reoptimizeRoute: true));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("4.25 km");
        result.Message.ShouldNotContain("may overlap");
        Received.InOrder(() =>
        {
            _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
            _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Is<ContainerTemplateLockScope>(scope => scope.Acquired), ContainerId, StorageThursday, false,
                Arg.Any<CancellationToken>());
            _mediator.Send(Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), Arg.Any<CancellationToken>());
        });
        await _routeWriteService.DidNotReceiveWithAnyArgs().OptimizeAndWriteAsync(default, default, default, default!, default);
    }

    [Test]
    public async Task FailedReoptimize_KeepsTheVerifiedBreakAndReportsSuccessWithTheReason()
    {
        // Arrange
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
            Arg.Is<ContainerTemplateLockScope>(scope => scope.Acquired), ContainerId, StorageThursday, false, Arg.Any<CancellationToken>())
            .Returns(ContainerRouteWriteResult.Failure("At least 2 visits are needed."));

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00", reoptimizeRoute: true));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("re-optimizing the route failed: At least 2 visits are needed.");
        result.Message.ShouldContain("may overlap the break");
        _stored[0].ContainerTemplateItems.Count(i => i.AbsenceId == SeededAbsenceIds.UnpaidBreak).ShouldBe(1);
    }

    [Test]
    public async Task MovableBreakWithReoptimize_IsWrittenFirstAndThenHandedToTheRouteWrite()
    {
        // Arrange
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Any<ContainerTemplateLockScope>(), ContainerId, StorageThursday, false, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _stored[0].ContainerTemplateItems.Count(i => i.AbsenceId == SeededAbsenceIds.UnpaidBreak && !i.StartItem.HasValue)
                    .ShouldBe(1);
                return new ContainerRouteWriteResult(null, "Base", "Base", 3, "01:30", false, [], 1);
            });

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, reoptimizeRoute: true));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("movable break");
        result.Message.ShouldContain("The route was re-optimized");
    }

    [Test]
    public async Task ThrowingReoptimize_KeepsTheVerifiedBreakAndReportsSuccess()
    {
        // Arrange
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Any<ContainerTemplateLockScope>(), ContainerId, StorageThursday, false, Arg.Any<CancellationToken>())
            .Returns<Task<ContainerRouteWriteResult>>(_ => throw new InvalidOperationException("routing down"));

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00", reoptimizeRoute: true));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("re-optimizing the route failed");
        result.Message.ShouldNotContain("routing down");
        _stored[0].ContainerTemplateItems.Count(i => i.AbsenceId == SeededAbsenceIds.UnpaidBreak).ShouldBe(1);
        await _mediator.Received(1).Send(
            Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnlyAHolidayTemplate_GetsTheBreakAndIsReoptimizedAsHoliday()
    {
        // Arrange
        var holiday = Template(Visit(VisitMorning, new TimeOnly(8, 0)));
        holiday.IsHoliday = true;
        _stored = [holiday];
        _routeWriteService.OptimizeAndWriteUnderHeldLockAsync(
                Arg.Any<ContainerTemplateLockScope>(), ContainerId, StorageThursday, true, Arg.Any<CancellationToken>())
            .Returns(ContainerRouteWriteResult.Failure("At least 2 visits are needed."));

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, startTime: "12:00", reoptimizeRoute: true));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("(holiday variant)");
        _stored[0].ContainerTemplateItems.Count(i => i.AbsenceId == SeededAbsenceIds.UnpaidBreak).ShouldBe(1);
        await _routeWriteService.Received(1).OptimizeAndWriteUnderHeldLockAsync(
            Arg.Any<ContainerTemplateLockScope>(), ContainerId, StorageThursday, true, Arg.Any<CancellationToken>());
    }

    private void LockIs(bool alreadyHeld) =>
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = true, AlreadyHeld = alreadyHeld });

    private static List<Absence> Absences() =>
    [
        Absence(SeededAbsenceIds.UnpaidBreak, "Pause unbezahlt", "Unpaid break", "Pausa non retribuita", appliesToContainer: true),
        Absence(PaidBreakId, "Pause bezahlt", "Paid break", "Pausa retribuita", appliesToContainer: true),
        Absence(Guid.NewGuid(), "Ferien", "Vacation", "Vacanze", appliesToContainer: false)
    ];

    private static Absence Absence(Guid id, string de, string en, string it, bool appliesToContainer) => new()
    {
        Id = id,
        Name = new MultiLanguage { De = de, En = en, It = it },
        Abbreviation = new MultiLanguage(),
        Description = new MultiLanguage(),
        AppliesToContainer = appliesToContainer
    };

    private static ContainerTemplateResource Template(params ContainerTemplateItemResource[] items) =>
        Template(StorageThursday, items);

    private static ContainerTemplateResource Template(int storageWeekday, params ContainerTemplateItemResource[] items) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = ContainerId,
        Weekday = storageWeekday,
        FromTime = new TimeOnly(8, 0),
        UntilTime = new TimeOnly(17, 0),
        ContainerTemplateItems = items.ToList()
    };

    private static ContainerTemplateItemResource Visit(Guid shiftId, TimeOnly start) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shiftId,
        StartItem = start,
        EndItem = start.AddMinutes(45)
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

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserLanguage = "de",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(
        int weekday, string? startTime = null, int? durationMinutes = null, string? absenceName = null, bool? reoptimizeRoute = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["containerId"] = ContainerId.ToString(),
            ["weekday"] = weekday
        };
        if (startTime is not null)
        {
            parameters["startTime"] = startTime;
        }

        if (durationMinutes.HasValue)
        {
            parameters["durationMinutes"] = durationMinutes.Value;
        }

        if (absenceName is not null)
        {
            parameters["absenceName"] = absenceName;
        }

        if (reoptimizeRoute.HasValue)
        {
            parameters["reoptimizeRoute"] = reoptimizeRoute.Value;
        }

        return parameters;
    }
}
