// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// assign_container_week: books an employee on a container for every date of a range that has a template, in ONE
/// BulkAddWorksCommand, and returns every other date with its reason. The works repository stands in for the database:
/// the stubbed bulk command stores the works (and, on request, their sub-works) and the skill's re-read sees them, so a
/// pipeline that books less than it was asked to is caught as an error instead of reported as success.
/// </summary>

using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AssignContainerWeekSkillTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid PlannableId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid VisitShiftId = Guid.NewGuid();
    private static readonly DateOnly Monday = new(2026, 11, 2);
    private static readonly DateOnly Tuesday = new(2026, 11, 3);
    private static readonly DateOnly Wednesday = new(2026, 11, 4);
    private static readonly DateOnly Thursday = new(2026, 11, 5);
    private static readonly DateOnly Friday = new(2026, 11, 6);
    private static readonly DateOnly Saturday = new(2026, 11, 7);
    private static readonly DateOnly Sunday = new(2026, 11, 8);

    private IMediator _mediator = null!;
    private IShiftRepository _shiftRepository = null!;
    private IShiftReader _shiftReader = null!;
    private IContainerTemplateRepository _templateRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IContainerWorkChildrenReadRepository _childrenRepository = null!;
    private IClientRepository _clientRepository = null!;
    private ISealedDayRepository _sealedDays = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private List<Work> _stored = null!;
    private BulkAddWorksCommand? _sent;
    private Func<BulkWorkItem, bool> _persistsItem = null!;
    private bool _createChildren;
    private Shift _container = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftReader = Substitute.For<IShiftReader>();
        _templateRepository = Substitute.For<IContainerTemplateRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _childrenRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _sealedDays = Substitute.For<ISealedDayRepository>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();

        _container = Container(ContainerId);
        _shiftRepository.Get(ContainerId).Returns(_container);
        _templateRepository.GetTemplatesForContainer(ContainerId).Returns(_ => Templates());
        _clientRepository.Exists(ClientId).Returns(true);
        _sealedDays.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)>());
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);

        _stored = [];
        _sent = null;
        _persistsItem = _ => true;
        _createChildren = true;
        _workRepository.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => _stored.ToList());
        _childrenRepository.GetChildWorkShiftIds(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<Guid, Guid>)_stored
                .Where(work => work.ParentWorkId == call.Arg<Guid>())
                .ToDictionary(work => work.Id, work => work.ShiftId));
        _mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sent = call.Arg<BulkAddWorksCommand>();
                StoreAsPipelineWould(_sent);
                return new BulkWorksResponse { SuccessCount = _sent.Request.Works.Count };
            });
    }

    [Test]
    public async Task EveryDateWithATemplate_IsBookedInOneCommand_AndTheOthersAreReportedWithReasons()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Sunday));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldBe(new[] { Monday, Wednesday, Friday });
        _sent.Request.Works.ShouldAllBe(w =>
            w.ClientId == ClientId && w.ShiftId == ContainerId && w.AnalyseToken == null
            && w.StartTime == new TimeOnly(8, 0) && w.EndTime == new TimeOnly(17, 0));
        _sent.PeriodCheck.ShouldBe(PlanningPeriodCheck.Enforce);

        var data = Json(result);
        data.GetProperty("Verified").GetBoolean().ShouldBeTrue();
        data.GetProperty("Booked").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "2026-11-02", "2026-11-04", "2026-11-06" });
        var skipped = data.GetProperty("Skipped").EnumerateArray()
            .ToDictionary(e => e.GetProperty("Date").GetString()!, e => e.GetProperty("Reason").GetString()!);
        skipped.Keys.ShouldBe(new[] { "2026-11-03", "2026-11-05", "2026-11-07", "2026-11-08" }, ignoreOrder: true);
        skipped["2026-11-03"].ShouldContain("no template for Tuesday");
        skipped["2026-11-07"].ShouldContain("does not run on Saturday");
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("2026-11-02, 2026-11-04, 2026-11-06");
    }

    [Test]
    public async Task ExistingBookingOnTheContainer_IsNotBookedTwice()
    {
        _stored.Add(new Work { Id = Guid.NewGuid(), ClientId = ClientId, ShiftId = ContainerId, CurrentDate = Wednesday });

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldBe(new[] { Monday, Friday });
        result.Message.ShouldContain("already booked on this container");
    }

    [Test]
    public async Task SubWorkOfAnotherDay_DoesNotCountAsAnExistingBooking()
    {
        _stored.Add(new Work
        {
            Id = Guid.NewGuid(), ClientId = ClientId, ShiftId = ContainerId, CurrentDate = Wednesday, ParentWorkId = Guid.NewGuid()
        });

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldContain(Wednesday);
    }

    [Test]
    public async Task SealedDay_IsLeftOutWithItsReason()
    {
        _sealedDays.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)> { (Friday, ClientId) });

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldBe(new[] { Monday, Wednesday });
        result.Message.ShouldContain("2026-11-06 (the day is sealed for the employee)");
    }

    [Test]
    public async Task DayOutsideThePlanningPeriod_IsLeftOutWithTheGuardsMessage()
    {
        var result = await Skill(TestPlanningPeriodGuard.Violating((ClientId, Monday))).ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldBe(new[] { Wednesday, Friday });
        result.Message.ShouldContain("2026-11-02 (outside the membership period)");
    }

    [Test]
    public async Task BlockingConflict_LeavesThatDateOut_WhileAWarningStaysAWarning()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(new List<ScheduleValidationNotificationDto>
            {
                Conflict(Monday, ScheduleValidationType.Error, "schedule.error-list.collision"),
                Conflict(Wednesday, ScheduleValidationType.Warning, "schedule.warning-list.rest")
            }));

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldBe(new[] { Wednesday, Friday });
        result.Message.ShouldContain("blocking schedule conflict (schedule.error-list.collision)");
        var warnings = Json(result).GetProperty("Warnings").EnumerateArray().ToList();
        warnings.ShouldHaveSingleItem().GetProperty("Date").GetString().ShouldBe("2026-11-04");
    }

    [Test]
    public async Task ContainerValidity_LimitsTheBookableDates()
    {
        _container.FromDate = Wednesday;
        _container.UntilDate = Friday.AddDays(-1);

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldBe(new[] { Wednesday });
        result.Message.ShouldContain("the container is valid only from 2026-11-04");
        result.Message.ShouldContain("the container is valid only until 2026-11-05");
    }

    [Test]
    public async Task NothingBookable_IsAnError_AndSendsNothing()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params(Saturday, Sunday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing was booked");
        result.Message.ShouldContain("does not run on Saturday");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PipelineThatBooksLessThanAsked_IsAnErrorNamingTheMissingDate()
    {
        _persistsItem = item => item.CurrentDate != Wednesday;

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("does not confirm it for: 2026-11-04");
        result.Message.ShouldContain("Confirmed: 2026-11-02, 2026-11-06");
        result.Message.ShouldContain("nothing was rolled back");
    }

    [Test]
    public async Task BookingWithoutSubWorksWhereTheTemplateHasVisits_IsNotConfirmed()
    {
        _createChildren = false;

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2026-11-02, 2026-11-04");
        result.Message.ShouldNotContain("Confirmed: 2026-11-02");
    }

    [Test]
    public async Task TemplateWithoutVisits_NeedsNoSubWorksToBeConfirmed()
    {
        _createChildren = false;
        _templateRepository.GetTemplatesForContainer(ContainerId).Returns(_ => [Template(1, 0)]);

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.Select(w => w.CurrentDate).ShouldBe(new[] { Monday });
    }

    [Test]
    public async Task RefusalOfThePipeline_IsAnErrorInsteadOfAnException()
    {
        _mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<BulkWorksResponse>>(_ => throw new InvalidRequestException("Day is outside the group period"));

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("was refused: Day is outside the group period");
        _stored.ShouldBeEmpty();
    }

    [Test]
    public async Task ValidationFailureOfThePipeline_IsAnError()
    {
        _mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<BulkWorksResponse>>(_ => throw new ValidationException(
                [new ValidationFailure("Works", "A work needs a shift")]));

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("A work needs a shift");
    }

    [Test]
    public async Task SealedOrderId_IsResolvedToThePlannableContainerCopy()
    {
        var order = Container(ContainerId);
        order.Status = ShiftStatus.SealedOrder;
        _shiftRepository.Get(ContainerId).Returns(order);
        _shiftReader.FindFirstByOriginalIdAsync(ContainerId, ShiftStatus.OriginalShift, Arg.Any<CancellationToken>())
            .Returns(Container(PlannableId));
        _shiftRepository.Get(PlannableId).Returns(Container(PlannableId));
        _templateRepository.GetTemplatesForContainer(PlannableId).Returns(_ => Templates());

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeTrue(result.Message);
        _sent!.Request.Works.ShouldAllBe(w => w.ShiftId == PlannableId);
        await _templateRepository.DidNotReceive().GetTemplatesForContainer(ContainerId);
    }

    [Test]
    public async Task SealedOrderWithoutPlannableCopy_IsRefused()
    {
        var order = Container(ContainerId);
        order.Status = ShiftStatus.SealedOrder;
        _shiftRepository.Get(ContainerId).Returns(order);

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no plannable container shift");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ShiftThatIsNoContainer_IsRefused()
    {
        _container.ShiftType = ShiftType.IsTask;

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not a container shift");
    }

    [Test]
    public async Task HiddenContainer_ReadsLikeAMissingOne()
    {
        var result = await Skill(shiftGuard: ShiftVisibilityGuards.HideAll()).ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HiddenClient_ReadsLikeAnUnknownOne()
    {
        var result = await Skill(clientGuard: SkillClientVisibility.Hiding(ClientId)).ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"Client {ClientId} not found");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ContainerWithoutTemplates_IsRefused()
    {
        _templateRepository.GetTemplatesForContainer(ContainerId).Returns(_ => []);

        var result = await Skill().ExecuteAsync(Ctx(), Params(Monday, Friday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no weekday template");
    }

    [Test]
    public async Task RangeBeyondTheCap_IsRefusedBeforeAnythingIsRead()
    {
        var result = await Skill().ExecuteAsync(
            Ctx(), Params(Monday, Monday.AddDays(AssignContainerWeekSkill.MaxRangeDays)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"at most {AssignContainerWeekSkill.MaxRangeDays} days");
        await _shiftRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task RangeOfExactlyTheCap_IsAccepted()
    {
        var result = await Skill().ExecuteAsync(
            Ctx(), Params(Monday, Monday.AddDays(AssignContainerWeekSkill.MaxRangeDays - 1)));

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task UntilBeforeFrom_IsRefused()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params(Friday, Monday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must not be before");
    }

    [Test]
    public async Task MissingFromDate_IsRefusedWithTheParameterName()
    {
        var parameters = Params(Monday, Friday);
        parameters.Remove("fromDate");

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("fromDate is required");
    }

    private AssignContainerWeekSkill Skill(
        IClientPlanningPeriodGuard? periodGuard = null,
        IShiftVisibilityGuard? shiftGuard = null,
        IClientVisibilityGuard? clientGuard = null) => new(
            _mediator,
            _shiftRepository,
            _shiftReader,
            _templateRepository,
            _workRepository,
            _childrenRepository,
            _clientRepository,
            clientGuard ?? SkillClientVisibility.AllVisible(),
            _sealedDays,
            periodGuard ?? TestPlanningPeriodGuard.NoViolations(),
            _conflictChecker,
            shiftGuard ?? ShiftVisibilityGuards.AllowAll());

    private void StoreAsPipelineWould(BulkAddWorksCommand command)
    {
        foreach (var item in command.Request.Works.Where(_persistsItem))
        {
            var parent = new Work
            {
                Id = Guid.NewGuid(), ClientId = item.ClientId, ShiftId = item.ShiftId, CurrentDate = item.CurrentDate
            };
            _stored.Add(parent);
            if (_createChildren)
            {
                _stored.Add(new Work
                {
                    Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = VisitShiftId, CurrentDate = item.CurrentDate,
                    ParentWorkId = parent.Id
                });
            }
        }
    }

    private static List<ContainerTemplate> Templates() => [Template(1, 2), Template(3, 1), Template(5, 1)];

    private static ContainerTemplate Template(int storageWeekday, int visits) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = ContainerId,
        Weekday = storageWeekday,
        ContainerTemplateItems = Enumerable.Range(0, visits)
            .Select(_ => new ContainerTemplateItem { Id = Guid.NewGuid(), ShiftId = VisitShiftId })
            .ToList()
    };

    private static Shift Container(Guid id) => new()
    {
        Id = id,
        Name = "Tour 1",
        ShiftType = ShiftType.IsContainer,
        Status = ShiftStatus.OriginalShift,
        FromDate = new DateOnly(2026, 1, 1),
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(17, 0),
        IsMonday = true,
        IsTuesday = true,
        IsWednesday = true,
        IsThursday = true,
        IsFriday = true
    };

    private static ScheduleValidationNotificationDto Conflict(DateOnly date, ScheduleValidationType type, string comment) => new()
    {
        Type = type, ClientId = ClientId, Date = date, Comment = comment
    };

    private static JsonElement Json(SkillResult result) =>
        JsonSerializer.SerializeToElement(result.Data);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserLanguage = "en",
        UserPermissions = new List<string> { "CanEditSchedule" }
    };

    private static Dictionary<string, object> Params(DateOnly from, DateOnly until) => new()
    {
        ["containerId"] = ContainerId.ToString(),
        ["clientId"] = ClientId.ToString(),
        ["fromDate"] = from,
        ["untilDate"] = until
    };
}
