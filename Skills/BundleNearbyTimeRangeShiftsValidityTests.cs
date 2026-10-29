// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// bundle_nearby_timerange_shifts_into_container with the real container autofill and available-task list over EF
/// InMemory (InMemoryContainerAutofill): the container is valid from fromDate to untilDate, and a TimeRange task whose
/// validity starts after that period is neither a candidate nor written into the template; with an open-ended container
/// the same task is bundled. Only the shift repository's write, the mediator commands and the lock are faked; the
/// container the repository "writes" lands in the InMemory database with the requested validity, as
/// ShiftRepository.AddWithSealedOrderHandling's plannable copy would.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BundleOutcome = Klacks.Api.Application.Skills.BundleNearbyTimeRangeShiftsIntoContainerSkill.BundleOutcome;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BundleNearbyTimeRangeShiftsValidityTests
{
    private const int IsoWednesday = 3;

    private static readonly DateOnly ThisMonthStart = new(2026, 11, 2);
    private static readonly DateOnly NextMonthStart = new(2026, 12, 2);

    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private IShiftRepository _shiftRepository = null!;
    private IMediator _mediator = null!;
    private PostContainerTemplatesCommand? _postCommand;
    private BundleNearbyTimeRangeShiftsIntoContainerSkill _skill = null!;
    private Shift _thisMonthTask = null!;
    private Shift _nextMonthTask = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _thisMonthTask = TaskShift("ThisMonth", ThisMonthStart, 1);
        _nextMonthTask = TaskShift("NextMonth", NextMonthStart, 2);
        await _context.Shift.AddRangeAsync(_thisMonthTask, _nextMonthTask);
        await _context.SaveChangesAsync();

        var (autofill, provider) = InMemoryContainerAutofill.Create(InMemoryContainerAutofill.AvailableTasks(_context));
        _provider = provider;

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).Returns(ci => AddPlannableCopyAsync(ci.Arg<Shift>()));
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(ci => _context.Shift.AsNoTracking().FirstOrDefault(s => s.Id == ci.Arg<Guid>()));

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<BundleOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<BundleOutcome>>>()());

        _postCommand = null;
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = Guid.NewGuid(), Acquired = true });
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        _mediator.Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _postCommand = ci.Arg<PostContainerTemplatesCommand>();
                return new List<ContainerTemplateResource>();
            });
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => _postCommand?.Resources.ToList() ?? []);

        var userService = Substitute.For<IUserService>();
        userService.GetInstanceId().Returns("instance-1");
        var macroResolver = Substitute.For<IDefaultShiftMacroResolver>();
        macroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);

        _skill = new BundleNearbyTimeRangeShiftsIntoContainerSkill(
            _shiftRepository,
            Substitute.For<IGroupRepository>(),
            _mediator,
            unitOfWork,
            userService,
            autofill,
            Substitute.For<ISettingsReader>(),
            Substitute.For<ISettingsEncryptionService>(),
            GeoServicesPolicyTestFactory.Default(),
            TestGroupWriteVisibility.UnrestrictedGroups(),
            Substitute.For<IGroupItemRepository>(),
            ShiftVisibilityGuards.AllowAll(),
            macroResolver,
            new ScheduleMapper());
    }

    [TearDown]
    public void TearDown()
    {
        _provider?.Dispose();
        _context?.Dispose();
    }

    [Test]
    public async Task ATaskValidOnlyNextMonth_IsNotBundled_IntoAContainerValidThisMonth()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(untilDate: "2026-11-30"));

        result.Success.ShouldBeTrue(result.Message);
        BundledShiftIds().ShouldBe([_thisMonthTask.Id]);
    }

    [Test]
    public async Task ATaskValidOnlyNextMonth_IsBundled_IntoAnOpenEndedContainer()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(untilDate: null));

        result.Success.ShouldBeTrue(result.Message);
        BundledShiftIds().ShouldBe([_thisMonthTask.Id, _nextMonthTask.Id], ignoreOrder: true);
    }

    private List<Guid> BundledShiftIds()
    {
        _postCommand.ShouldNotBeNull();
        return _postCommand.Resources.Single().ContainerTemplateItems.Select(item => item.ShiftId!.Value).ToList();
    }

    private async Task<Shift> AddPlannableCopyAsync(Shift submitted)
    {
        var copy = new Shift
        {
            Id = Guid.NewGuid(),
            Name = submitted.Name,
            Abbreviation = submitted.Abbreviation,
            Description = submitted.Description,
            Status = ShiftStatus.OriginalShift,
            ShiftType = submitted.ShiftType,
            OriginalId = submitted.Id,
            FromDate = submitted.FromDate,
            UntilDate = submitted.UntilDate,
            StartShift = submitted.StartShift,
            EndShift = submitted.EndShift,
            IsWednesday = submitted.IsWednesday,
        };
        await _context.Shift.AddAsync(copy);
        await _context.SaveChangesAsync();
        return copy;
    }

    private static Shift TaskShift(string name, DateOnly fromDate, double position) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Abbreviation = name,
        Description = name,
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsTask,
        FromDate = fromDate,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(17, 0),
        AfterShift = new TimeOnly(0, 0),
        BeforeShift = new TimeOnly(0, 0),
        WorkTime = 0.75m,
        IsTimeRange = true,
        IsWednesday = true,
        Client = InMemoryContainerAutofill.ClientAt(name, position)
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(string? untilDate)
    {
        var parameters = new Dictionary<string, object>
        {
            ["location"] = InMemoryContainerAutofill.BaseAddress,
            ["weekday"] = IsoWednesday,
            ["fromTime"] = "08:00",
            ["untilTime"] = "18:00",
            ["fromDate"] = "2026-11-01",
            ["transportMode"] = "car"
        };
        if (untilDate != null)
        {
            parameters["untilDate"] = untilDate;
        }

        return parameters;
    }
}
