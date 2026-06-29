// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetScheduleEntriesQueryHandler: filter pass-through and GroupItem validity enrichment.
/// </summary>
using Shouldly;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Application.Handlers.ScheduleEntries;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ScheduleEntries;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using DomainWorkFilter = Klacks.Api.Domain.Models.Filters.WorkFilter;

namespace Klacks.UnitTest.Handlers.ScheduleEntries;

[TestFixture]
public class GetScheduleEntriesQueryHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private IShiftGroupFilterService _groupFilterService = null!;
    private IClientAvailabilityScheduleService _clientAvailabilityScheduleService = null!;
    private DataBaseContext _context = null!;
    private GetScheduleEntriesQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        var groupItemRepository = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());

        _workRepository = Substitute.For<IWorkRepository>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _groupFilterService = Substitute.For<IShiftGroupFilterService>();
        _clientAvailabilityScheduleService = Substitute.For<IClientAvailabilityScheduleService>();

        _scheduleEntriesService.GetScheduleEntriesQuery(default, default, default, default)
            .ReturnsForAnyArgs(new TestAsyncEnumerable<ScheduleCell>(Enumerable.Empty<ScheduleCell>()));
        _clientAvailabilityScheduleService.GetClientAvailabilityQuery(default, default, default)
            .ReturnsForAnyArgs(new TestAsyncEnumerable<ClientAvailabilityScheduleEntry>(Enumerable.Empty<ClientAvailabilityScheduleEntry>()));
        _workRepository.GetPeriodHoursForClients(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new Dictionary<Guid, PeriodHoursResource>()));

        _handler = new GetScheduleEntriesQueryHandler(
            _scheduleEntriesService,
            _groupFilterService,
            _workRepository,
            new ScheduleMapper(),
            _clientAvailabilityScheduleService,
            groupItemRepository,
            Substitute.For<ILogger<GetScheduleEntriesQueryHandler>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Handle_SearchStringPassedToWorkFilter()
    {
        DomainWorkFilter? capturedFilter = null;
        _workRepository.WorkList(
                Arg.Do<DomainWorkFilter>(f => capturedFilter = f),
                Arg.Any<CancellationToken>())
            .Returns((new List<Client>(), 0));
        _groupFilterService.GetVisibleGroupIdsAsync(default).ReturnsForAnyArgs(new List<Guid>());

        var filter = new WorkScheduleFilter
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            SearchString = "test",
            OrderBy = "name",
            SortOrder = "asc",
            ShowEmployees = true,
            ShowExtern = true,
            StartRow = 0,
            RowCount = 200,
            PaymentInterval = 2,
        };

        await _handler.Handle(new GetScheduleEntriesQuery(filter), CancellationToken.None);

        capturedFilter.ShouldNotBeNull();
        capturedFilter!.SearchString.ShouldBe("test");
    }

    [Test]
    public async Task Handle_NoGroupFilter_GroupItemDatesNotSet()
    {
        var clientId = Guid.NewGuid();
        SetupWorkListWithClient(clientId);
        _groupFilterService.GetVisibleGroupIdsAsync(null).ReturnsForAnyArgs(new List<Guid>());

        var result = await _handler.Handle(CreateQuery(), CancellationToken.None);

        result.Clients.ShouldHaveSingleItem();
        result.Clients[0].GroupItemValidFrom.ShouldBeNull();
        result.Clients[0].GroupItemValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task Handle_ClientInVisibleGroup_SetsGroupItemDates()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        SetupWorkListWithClient(clientId);
        _groupFilterService.GetVisibleGroupIdsAsync(groupId).ReturnsForAnyArgs(new List<Guid> { groupId });

        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            ValidFrom = new DateTime(2026, 1, 15),
            ValidUntil = new DateTime(2026, 1, 25),
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(CreateQuery(groupId), CancellationToken.None);

        result.Clients[0].GroupItemValidFrom.ShouldBe(new DateOnly(2026, 1, 15));
        result.Clients[0].GroupItemValidUntil.ShouldBe(new DateOnly(2026, 1, 25));
    }

    [Test]
    public async Task Handle_ClientNotInVisibleGroup_GroupItemDatesNotSet()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var otherGroupId = Guid.NewGuid();
        SetupWorkListWithClient(clientId);
        _groupFilterService.GetVisibleGroupIdsAsync(groupId).ReturnsForAnyArgs(new List<Guid> { groupId });

        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = otherGroupId,
            ValidFrom = new DateTime(2026, 1, 15),
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(CreateQuery(groupId), CancellationToken.None);

        result.Clients[0].GroupItemValidFrom.ShouldBeNull();
        result.Clients[0].GroupItemValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task Handle_MultipleGroupItems_UsesWidestRange()
    {
        var clientId = Guid.NewGuid();
        var group1 = Guid.NewGuid();
        var group2 = Guid.NewGuid();
        SetupWorkListWithClient(clientId);
        _groupFilterService.GetVisibleGroupIdsAsync(group1).ReturnsForAnyArgs(new List<Guid> { group1, group2 });

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = group1, ValidFrom = new DateTime(2026, 1, 10), ValidUntil = new DateTime(2026, 1, 20), IsDeleted = false },
            new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = group2, ValidFrom = new DateTime(2026, 1, 5), ValidUntil = new DateTime(2026, 1, 25), IsDeleted = false });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(CreateQuery(group1), CancellationToken.None);

        result.Clients[0].GroupItemValidFrom.ShouldBe(new DateOnly(2026, 1, 5));
        result.Clients[0].GroupItemValidUntil.ShouldBe(new DateOnly(2026, 1, 25));
    }

    [Test]
    public async Task Handle_DeletedGroupItem_GroupItemDatesNotSet()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        SetupWorkListWithClient(clientId);
        _groupFilterService.GetVisibleGroupIdsAsync(groupId).ReturnsForAnyArgs(new List<Guid> { groupId });

        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            ValidFrom = new DateTime(2026, 1, 15),
            IsDeleted = true,
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(CreateQuery(groupId), CancellationToken.None);

        result.Clients[0].GroupItemValidFrom.ShouldBeNull();
    }

    [Test]
    public async Task Handle_GroupItemNullValidUntil_SetsOnlyValidFrom()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        SetupWorkListWithClient(clientId);
        _groupFilterService.GetVisibleGroupIdsAsync(groupId).ReturnsForAnyArgs(new List<Guid> { groupId });

        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            ValidFrom = new DateTime(2026, 1, 15),
            ValidUntil = null,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(CreateQuery(groupId), CancellationToken.None);

        result.Clients[0].GroupItemValidFrom.ShouldBe(new DateOnly(2026, 1, 15));
        result.Clients[0].GroupItemValidUntil.ShouldBeNull();
    }

    private GetScheduleEntriesQuery CreateQuery(Guid? selectedGroup = null) =>
        new(new WorkScheduleFilter
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            StartRow = 0,
            RowCount = 200,
            PaymentInterval = 2,
            SelectedGroup = selectedGroup,
        });

    private void SetupWorkListWithClient(Guid clientId)
    {
        var client = new Client { Id = clientId };
        _workRepository.WorkList(Arg.Any<DomainWorkFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<Client> { client }, 1));
    }
}
