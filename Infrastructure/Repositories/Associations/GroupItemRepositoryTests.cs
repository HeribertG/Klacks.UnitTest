// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GroupItemRepository.GetByClientAndGroup: it returns only the real membership of a
/// client in a group. A scenario membership (AnalyseToken set) may coexist with the real one for the
/// same client and group, and must never be handed out — callers soft-delete or count what they get,
/// which would destroy a running analysis scenario and skip the real change.
/// GetValidityRangesAsync: the group period of an employee is the widest range over its items in the given groups, and
/// an item without a start or without an end leaves that side open - an open-ended item must not be narrowed by a
/// bounded one (LINQ Min/Max skip null and would otherwise do exactly that).
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Associations;

[TestFixture]
public class GroupItemRepositoryTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid AnalyseToken = Guid.NewGuid();

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
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task GetByClientAndGroup_OnlyScenarioMembershipExists_ReturnsNull()
    {
        AddMembership(AnalyseToken);
        await _context.SaveChangesAsync();

        var result = await _repository.GetByClientAndGroup(ClientId, GroupId);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetByClientAndGroup_RealAndScenarioMembershipCoexist_ReturnsTheRealOne()
    {
        var realMembership = AddMembership(null);
        AddMembership(AnalyseToken);
        await _context.SaveChangesAsync();

        var result = await _repository.GetByClientAndGroup(ClientId, GroupId);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(realMembership.Id);
        result.AnalyseToken.ShouldBeNull();
    }

    [Test]
    public async Task GetByClientAndGroup_RealMembershipSoftDeleted_ReturnsNull()
    {
        var realMembership = AddMembership(null);
        realMembership.IsDeleted = true;
        await _context.SaveChangesAsync();

        var result = await _repository.GetByClientAndGroup(ClientId, GroupId);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetValidityRanges_AnOpenEndedItemBesideABoundedOne_LeavesTheEndOpen()
    {
        AddRange(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
        AddRange(new DateTime(2026, 2, 1), null);
        await _context.SaveChangesAsync();

        var ranges = await _repository.GetValidityRangesAsync([ClientId], [GroupId]);

        ranges[ClientId].ValidFrom.ShouldBe(new DateTime(2026, 1, 1));
        ranges[ClientId].ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task GetValidityRanges_AnItemWithoutStartBesideABoundedOne_LeavesTheStartOpen()
    {
        AddRange(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
        AddRange(null, new DateTime(2026, 2, 28));
        await _context.SaveChangesAsync();

        var ranges = await _repository.GetValidityRangesAsync([ClientId], [GroupId]);

        ranges[ClientId].ValidFrom.ShouldBeNull();
        ranges[ClientId].ValidUntil.ShouldBe(new DateTime(2026, 3, 31));
    }

    [Test]
    public async Task GetValidityRanges_OnlyBoundedItems_SpanTheEarliestStartToTheLatestEnd()
    {
        AddRange(new DateTime(2026, 1, 10), new DateTime(2026, 1, 20));
        AddRange(new DateTime(2026, 1, 5), new DateTime(2026, 1, 25));
        await _context.SaveChangesAsync();

        var ranges = await _repository.GetValidityRangesAsync([ClientId], [GroupId]);

        ranges[ClientId].ShouldBe((new DateTime(2026, 1, 5), new DateTime(2026, 1, 25)));
    }

    private void AddRange(DateTime? validFrom, DateTime? validUntil)
    {
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            GroupId = GroupId,
            ValidFrom = validFrom,
            ValidUntil = validUntil
        });
    }

    private GroupItem AddMembership(Guid? analyseToken)
    {
        var item = new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            GroupId = GroupId,
            AnalyseToken = analyseToken
        };

        _context.GroupItem.Add(item);
        return item;
    }

    private GroupItem AddItem(DateTime? validFrom, bool deleted = false)
    {
        var item = new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            GroupId = GroupId,
            ValidFrom = validFrom,
            IsDeleted = deleted
        };
        _context.GroupItem.Add(item);
        return item;
    }

    [Test]
    public async Task PullForwardValidFromAsync_MovesALaterStartForward_NeverLaterAndNeverAnOpenOrDeletedOne()
    {
        var later = AddItem(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        var earlier = AddItem(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var open = AddItem(null);
        var deleted = AddItem(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), deleted: true);
        await _context.SaveChangesAsync();
        var target = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var changed = await _repository.PullForwardValidFromAsync(
            new Dictionary<Guid, DateTime> { [later.Id] = target, [earlier.Id] = target, [open.Id] = target, [deleted.Id] = target });
        await _context.SaveChangesAsync();

        changed.ShouldBe(1);
        var stored = await _repository.GetValidFromByIdsAsync([later.Id, earlier.Id, open.Id, deleted.Id]);
        stored[later.Id].ShouldBe(target);
        stored[earlier.Id].ShouldBe(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        stored[open.Id].ShouldBeNull();
        stored.ContainsKey(deleted.Id).ShouldBeFalse();
    }

    [Test]
    public async Task PullForwardValidFromAsync_FiveThousandItems_AreWrittenCorrectly()
    {
        var items = Enumerable.Range(0, 5000).Select(_ => AddItem(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc))).ToList();
        await _context.SaveChangesAsync();
        var target = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var changed = await _repository.PullForwardValidFromAsync(items.ToDictionary(i => i.Id, _ => target));
        await _context.SaveChangesAsync();

        changed.ShouldBe(5000);
        var stored = await _repository.GetValidFromByIdsAsync(items.Select(i => i.Id).ToList());
        stored.Count.ShouldBe(5000);
        stored.Values.ShouldAllBe(value => value == target);
    }

    [Test]
    public async Task PullForwardValidFromAsync_IsRetrySafe_TheSecondCallBeforeASaveStillCountsTheItem()
    {
        var later = AddItem(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        await _context.SaveChangesAsync();
        var target = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var map = new Dictionary<Guid, DateTime> { [later.Id] = target };

        var first = await _repository.PullForwardValidFromAsync(map);
        var second = await _repository.PullForwardValidFromAsync(map);
        await _context.SaveChangesAsync();
        var third = await _repository.PullForwardValidFromAsync(map);

        first.ShouldBe(1);
        second.ShouldBe(1);
        third.ShouldBe(0);
        (await _repository.GetValidFromByIdsAsync([later.Id]))[later.Id].ShouldBe(target);
    }

    [Test]
    public async Task GetEndedMembershipsAsync_ReturnsOnlyRealEndedClientMemberships()
    {
        var clientId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ended = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = GroupId, ValidUntil = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc) };
        var open = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = other };
        var scenario = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = other, ValidUntil = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc), AnalyseToken = Guid.NewGuid() };
        var shift = new GroupItem { Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), GroupId = other, ValidUntil = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc) };
        var deleted = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = other, ValidUntil = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc), IsDeleted = true };
        _context.GroupItem.AddRange(ended, open, scenario, shift, deleted);
        await _context.SaveChangesAsync();

        var result = await _repository.GetEndedMembershipsAsync([clientId]);

        result.Count.ShouldBe(1);
        result[0].ClientId.ShouldBe(clientId);
        result[0].GroupId.ShouldBe(GroupId);
        result[0].ValidUntil.ShouldBe(new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GetByClientAndGroup_WithSeveralRows_PrefersTheActiveOne_ThenTheLatestStart()
    {
        var clientId = Guid.NewGuid();
        var endedLate = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = GroupId, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ValidUntil = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
        var activeOld = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = GroupId, ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var activeNew = new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = GroupId, ValidFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        _context.GroupItem.AddRange(endedLate, activeOld, activeNew);
        await _context.SaveChangesAsync();

        var result = await _repository.GetByClientAndGroup(clientId, GroupId);

        result!.Id.ShouldBe(activeNew.Id);
    }
}
