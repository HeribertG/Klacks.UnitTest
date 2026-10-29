// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// bundle_nearby_timerange_shifts_into_container with the real container autofill, available-task list and
/// GroupItemRepository over EF InMemory (InMemoryContainerAutofill): a group passed by name becomes the container's
/// group, and only the TimeRange tasks linked to that group are bundled, while the task of another group stays out.
/// Without a group both tasks are bundled. The group repository, the shift repository's write, the mediator commands
/// and the lock are faked; the container the repository "writes" lands in the InMemory database together with its
/// group items, as ShiftRepository.AddWithSealedOrderHandling's plannable copy (ShiftCloner) would.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BundleOutcome = Klacks.Api.Application.Skills.BundleNearbyTimeRangeShiftsIntoContainerSkill.BundleOutcome;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BundleNearbyTimeRangeShiftsGroupFilterTests
{
    private const int IsoWednesday = 3;
    private const string WinterthurGroupName = "Spitex Winterthur";
    private const string ZurichGroupName = "Spitex Zürich";

    private static readonly DateOnly TaskStart = new(2026, 11, 2);

    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private PostContainerTemplatesCommand? _postCommand;
    private BundleNearbyTimeRangeShiftsIntoContainerSkill _skill = null!;
    private Shift _winterthurTask = null!;
    private Shift _zurichTask = null!;
    private Group _winterthurGroup = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _winterthurGroup = new Group { Id = Guid.NewGuid(), Name = WinterthurGroupName };
        var zurichGroup = new Group { Id = Guid.NewGuid(), Name = ZurichGroupName };
        _winterthurTask = TaskShift("Winterthur", 1, _winterthurGroup.Id);
        _zurichTask = TaskShift("Zurich", 2, zurichGroup.Id);
        await _context.Group.AddRangeAsync(_winterthurGroup, zurichGroup);
        await _context.Shift.AddRangeAsync(_winterthurTask, _zurichTask);
        await _context.SaveChangesAsync();

        var (autofill, provider) = InMemoryContainerAutofill.Create(InMemoryContainerAutofill.AvailableTasks(_context));
        _provider = provider;

        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).Returns(ci => AddPlannableCopyAsync(ci.Arg<Shift>()));
        shiftRepository.Get(Arg.Any<Guid>()).Returns(ci => _context.Shift.AsNoTracking().FirstOrDefault(s => s.Id == ci.Arg<Guid>()));

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(_ => _context.Group.AsNoTracking().ToList());
        groupRepository.Get(Arg.Any<Guid>()).Returns(ci => _context.Group.AsNoTracking().FirstOrDefault(g => g.Id == ci.Arg<Guid>()));

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<BundleOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<BundleOutcome>>>()());

        _postCommand = null;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = Guid.NewGuid(), Acquired = true });
        mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        mediator.Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _postCommand = ci.Arg<PostContainerTemplatesCommand>();
                return new List<ContainerTemplateResource>();
            });
        mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => _postCommand?.Resources.ToList() ?? []);

        var userService = Substitute.For<IUserService>();
        userService.GetInstanceId().Returns("instance-1");
        var macroResolver = Substitute.For<IDefaultShiftMacroResolver>();
        macroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);

        _skill = new BundleNearbyTimeRangeShiftsIntoContainerSkill(
            shiftRepository,
            groupRepository,
            mediator,
            unitOfWork,
            userService,
            autofill,
            Substitute.For<ISettingsReader>(),
            Substitute.For<ISettingsEncryptionService>(),
            GeoServicesPolicyTestFactory.Default(),
            TestGroupWriteVisibility.UnrestrictedGroups(),
            new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>()),
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
    public async Task GroupName_BundlesOnlyTheTasksOfThatGroup()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(WinterthurGroupName));

        result.Success.ShouldBeTrue(result.Message);
        BundledShiftIds().ShouldBe([_winterthurTask.Id]);
    }

    [Test]
    public async Task WithoutGroup_TheTasksOfAllGroupsAreBundled()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(groupName: null));

        result.Success.ShouldBeTrue(result.Message);
        BundledShiftIds().ShouldBe([_winterthurTask.Id, _zurichTask.Id], ignoreOrder: true);
    }

    private List<Guid> BundledShiftIds()
    {
        _postCommand.ShouldNotBeNull();
        return _postCommand.Resources.Single().ContainerTemplateItems.Select(item => item.ShiftId!.Value).ToList();
    }

    private async Task<Shift> AddPlannableCopyAsync(Shift submitted)
    {
        var copyId = Guid.NewGuid();
        var copy = new Shift
        {
            Id = copyId,
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
            GroupItems = submitted.GroupItems
                .Select(item => new GroupItem { Id = Guid.NewGuid(), GroupId = item.GroupId, ShiftId = copyId, ValidFrom = item.ValidFrom })
                .ToList()
        };
        await _context.Shift.AddAsync(copy);
        await _context.SaveChangesAsync();
        return copy;
    }

    private static Shift TaskShift(string name, double position, Guid groupId)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = name,
            Abbreviation = name,
            Description = name,
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = TaskStart,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(17, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 0.75m,
            IsTimeRange = true,
            IsWednesday = true,
            Client = InMemoryContainerAutofill.ClientAt(name, position),
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = id }]
        };
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(string? groupName)
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
        if (groupName != null)
        {
            parameters["groupName"] = groupName;
        }

        return parameters;
    }
}
