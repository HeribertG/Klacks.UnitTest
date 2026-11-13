// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GroupItemRepository.GetBorrowedOnlyClientIdsAsync names the people who are only borrowed into a set of groups: a
/// temporary membership of the real plan in one of them and no regular membership in any of them. A regular membership
/// anywhere in the set (a subgroup included) keeps the person; a scenario membership counts on neither side; a person
/// without any membership in the set is not named, so a planning run never loses somebody the client listed for another
/// reason. The Npgsql translation is proven in Klacks.IntegrationTest TemporaryMembershipPerDayTests.
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Associations;

[TestFixture]
public class GroupItemRepositoryBorrowedOnlyTests
{
    private static readonly DateTime BorrowedDay = new(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _group = Guid.NewGuid();
    private readonly Guid _subgroup = Guid.NewGuid();
    private DataBaseContext _context = null!;
    private GroupItemRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task OnlyTemporaryMembership_IsBorrowed()
    {
        var anna = Guid.NewGuid();
        AddTemporaryDay(anna, _group);
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([anna], [_group, _subgroup]);

        borrowed.ShouldHaveSingleItem().ShouldBe(anna);
    }

    [Test]
    public async Task RegularAndTemporaryMembershipInTheSameGroup_IsNotBorrowed()
    {
        var ben = Guid.NewGuid();
        AddRegular(ben, _group);
        AddTemporaryDay(ben, _group);
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([ben], [_group, _subgroup]);

        borrowed.ShouldBeEmpty();
    }

    [Test]
    public async Task NoMembershipInTheGroups_IsNotBorrowed()
    {
        var carla = Guid.NewGuid();
        AddRegular(carla, Guid.NewGuid());
        AddTemporaryDay(carla, Guid.NewGuid());
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([carla], [_group, _subgroup]);

        borrowed.ShouldBeEmpty();
    }

    [Test]
    public async Task RegularMembershipInASubgroup_IsNotBorrowed()
    {
        var dora = Guid.NewGuid();
        AddRegular(dora, _subgroup);
        AddTemporaryDay(dora, _group);
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([dora], [_group, _subgroup]);

        borrowed.ShouldBeEmpty();
    }

    [Test]
    public async Task ScenarioMembership_DoesNotCountAsRegular()
    {
        var emil = Guid.NewGuid();
        AddTemporaryDay(emil, _group);
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = emil, GroupId = _group, AnalyseToken = Guid.NewGuid() });
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([emil], [_group, _subgroup]);

        borrowed.ShouldHaveSingleItem().ShouldBe(emil);
    }

    [Test]
    public async Task OnlyAScenarioMembership_IsNotBorrowed()
    {
        var fritz = Guid.NewGuid();
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(), ClientId = fritz, GroupId = _group, ValidFrom = BorrowedDay, ValidUntil = BorrowedDay,
            IsTemporary = true, AnalyseToken = Guid.NewGuid()
        });
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([fritz], [_group, _subgroup]);

        borrowed.ShouldBeEmpty();
    }

    [Test]
    public async Task DeletedRegularMembership_DoesNotKeepTheBorrowedPerson()
    {
        var gina = Guid.NewGuid();
        AddRegular(gina, _group).IsDeleted = true;
        AddTemporaryDay(gina, _group);
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([gina], [_group, _subgroup]);

        borrowed.ShouldHaveSingleItem().ShouldBe(gina);
    }

    [Test]
    public async Task OnlyTheAskedClients_AreRead()
    {
        var asked = Guid.NewGuid();
        var notAsked = Guid.NewGuid();
        AddTemporaryDay(asked, _group);
        AddTemporaryDay(notAsked, _group);
        await _context.SaveChangesAsync();

        var borrowed = await _repository.GetBorrowedOnlyClientIdsAsync([asked], [_group]);

        borrowed.ShouldHaveSingleItem().ShouldBe(asked);
    }

    private GroupItem AddRegular(Guid clientId, Guid groupId)
    {
        var item = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = groupId };
        _context.GroupItem.Add(item);
        return item;
    }

    private void AddTemporaryDay(Guid clientId, Guid groupId)
        => _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            ValidFrom = BorrowedDay,
            ValidUntil = BorrowedDay,
            IsTemporary = true
        });
}
