// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The master data of an employee knows regular memberships only (RegularMembership). Reading a client lists neither
/// the temporary memberships of borrowed days nor the memberships a scenario holds, and saving the client with what was
/// read neither deletes those rows nor turns them into regular ones, also when the context already tracks them next to
/// the client (EF then puts them into the navigation whatever the query filtered). Runs ClientRepository with the real
/// ClientValidator on the in-memory provider; the same save against PostgreSQL is in TemporaryMembershipPerDayTests.
/// </summary>

using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientRepositoryTemporaryMembershipTests
{
    private static readonly DateTime Monday = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Friday = new(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _homeGroupId = Guid.NewGuid();
    private readonly Guid _borrowingGroupId = Guid.NewGuid();
    private readonly Guid _scenarioGroupId = Guid.NewGuid();
    private readonly Guid _scenarioToken = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private ClientRepository _repository = null!;
    private GroupItem _regular = null!;
    private GroupItem _borrowedMonday = null!;
    private GroupItem _borrowedFriday = null!;
    private GroupItem _scenarioDay = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            new ClientValidator(),
            Substitute.For<ILogger<ClientRepository>>());

        _context.Client.Add(new Client { Id = _clientId, Name = "Borrowed", FirstName = "Person", Gender = GenderEnum.Female });
        _context.Group.AddRange(
            new Group { Id = _homeGroupId, Name = "Home" },
            new Group { Id = _borrowingGroupId, Name = "Borrowing" },
            new Group { Id = _scenarioGroupId, Name = "Scenario" });
        _regular = new GroupItem { Id = Guid.NewGuid(), ClientId = _clientId, GroupId = _homeGroupId };
        _borrowedMonday = TemporaryDay(_borrowingGroupId, Monday, token: null);
        _borrowedFriday = TemporaryDay(_borrowingGroupId, Friday, token: null);
        _scenarioDay = TemporaryDay(_scenarioGroupId, Monday, _scenarioToken);
        _context.GroupItem.AddRange(_regular, _borrowedMonday, _borrowedFriday, _scenarioDay);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Get_ListsTheRegularMembershipOnly()
    {
        // Act
        var client = await _repository.Get(_clientId);

        // Assert
        client.ShouldNotBeNull();
        client.GroupItems.ShouldHaveSingleItem().Id.ShouldBe(_regular.Id);
    }

    [Test]
    public async Task GetTrackedForUpdate_ListsTheRegularMembershipOnly()
    {
        // Act
        var client = await _repository.GetTrackedForUpdate(_clientId);

        // Assert
        client.ShouldNotBeNull();
        client.GroupItems.ShouldHaveSingleItem().Id.ShouldBe(_regular.Id);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Put_WithTheMembershipsThatWereRead_LeavesTheBorrowedDaysAndTheScenarioRow(bool rowsAlreadyTracked)
    {
        // Arrange
        if (rowsAlreadyTracked)
        {
            _ = await _context.GroupItem.IgnoreQueryFilters().Where(gi => gi.ClientId == _clientId).ToListAsync();
        }

        // Act
        await _repository.Put(Incoming(_homeGroupId));
        await _context.SaveChangesAsync();

        // Assert
        var stored = await StoredItemsAsync();
        stored.Select(gi => gi.Id).ShouldBe(
            [_regular.Id, _borrowedMonday.Id, _borrowedFriday.Id, _scenarioDay.Id], ignoreOrder: true);
        ShouldBeUntouched(stored.Single(gi => gi.Id == _borrowedMonday.Id), _borrowedMonday);
        ShouldBeUntouched(stored.Single(gi => gi.Id == _borrowedFriday.Id), _borrowedFriday);
        ShouldBeUntouched(stored.Single(gi => gi.Id == _scenarioDay.Id), _scenarioDay);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Put_ThatNamesTheGroupOfTheBorrowedDays_AddsARegularMembership_AndKeepsTheBorrowedDays(bool rowsAlreadyTracked)
    {
        // Arrange
        if (rowsAlreadyTracked)
        {
            _ = await _context.GroupItem.IgnoreQueryFilters().Where(gi => gi.ClientId == _clientId).ToListAsync();
        }

        // Act
        await _repository.Put(Incoming(_homeGroupId, _borrowingGroupId));
        await _context.SaveChangesAsync();

        // Assert
        var inBorrowingGroup = (await StoredItemsAsync()).Where(gi => gi.GroupId == _borrowingGroupId).ToList();
        inBorrowingGroup.Count(gi => !gi.IsTemporary).ShouldBe(1);
        ShouldBeUntouched(inBorrowingGroup.Single(gi => gi.Id == _borrowedMonday.Id), _borrowedMonday);
        ShouldBeUntouched(inBorrowingGroup.Single(gi => gi.Id == _borrowedFriday.Id), _borrowedFriday);
    }

    [Test]
    public async Task Put_ThatLeavesTheRegularMembershipOut_RemovesOnlyThat()
    {
        // Act
        await _repository.Put(Incoming(_borrowingGroupId));
        await _context.SaveChangesAsync();

        // Assert
        var stored = await StoredItemsAsync();
        stored.Select(gi => gi.Id).ShouldNotContain(_regular.Id);
        stored.Select(gi => gi.Id).ShouldContain(_borrowedMonday.Id);
        stored.Select(gi => gi.Id).ShouldContain(_borrowedFriday.Id);
        stored.Select(gi => gi.Id).ShouldContain(_scenarioDay.Id);
    }

    private Client Incoming(params Guid[] groupIds) => new()
    {
        Id = _clientId,
        Name = "Borrowed",
        FirstName = "Person",
        Gender = GenderEnum.Female,
        GroupItems = groupIds
            .Select(groupId => new GroupItem { Id = Guid.NewGuid(), ClientId = _clientId, GroupId = groupId })
            .ToList(),
    };

    private GroupItem TemporaryDay(Guid groupId, DateTime day, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _clientId,
        GroupId = groupId,
        ValidFrom = day,
        ValidUntil = day,
        AnalyseToken = token,
        IsTemporary = true,
    };

    private Task<List<GroupItem>> StoredItemsAsync()
        => _context.GroupItem.AsNoTracking().Where(gi => gi.ClientId == _clientId).ToListAsync();

    private static void ShouldBeUntouched(GroupItem stored, GroupItem seeded)
    {
        stored.IsDeleted.ShouldBeFalse();
        stored.IsTemporary.ShouldBeTrue();
        stored.ClientId.ShouldBe(seeded.ClientId);
        stored.AnalyseToken.ShouldBe(seeded.AnalyseToken);
        stored.GroupId.ShouldBe(seeded.GroupId);
        stored.ValidFrom.ShouldBe(seeded.ValidFrom);
        stored.ValidUntil.ShouldBe(seeded.ValidUntil);
    }
}
