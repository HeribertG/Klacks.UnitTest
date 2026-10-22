// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes GetAvailableShiftsQueryHandler over the real GroupItemRepository (EF InMemory): the shifts linked
/// to the groups of the client's group tree, each shift once, ordered by name; deleted links, deleted shifts,
/// shift-less items and groups outside the tree contribute nothing.
/// </summary>

using Klacks.Api.Application.Handlers.ClientShiftPreferences;
using Klacks.Api.Application.Queries.ClientShiftPreferences;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ClientShiftPreferences;

[TestFixture]
public class GetAvailableShiftsOverRepositoryTests
{
    private DataBaseContext _context = null!;
    private GetAvailableShiftsQueryHandler _handler = null!;

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _rootId = Guid.NewGuid();
    private readonly Guid _childId = Guid.NewGuid();
    private readonly Guid _foreignGroupId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _handler = new GetAvailableShiftsQueryHandler(
            new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>()),
            guard,
            Substitute.For<ILogger<GetAvailableShiftsQueryHandler>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ListsEveryShiftOfTheTreeOnce_OrderedByName_WithoutDeletedOrForeignLinks()
    {
        _context.Group.AddRange(
            new Group { Id = _rootId, Name = "root" },
            new Group { Id = _childId, Name = "child", Root = _rootId, Parent = _rootId },
            new Group { Id = _foreignGroupId, Name = "foreign" });

        var bravo = Shift("bravo", "B");
        var alpha = Shift("alpha", "A");
        var charlie = Shift("charlie", "C");
        var deletedShift = Shift("delta", "D");
        deletedShift.IsDeleted = true;
        var foreign = Shift("echo", "E");
        var unlinkedByDeletion = Shift("foxtrot", "F");
        _context.Shift.AddRange(bravo, alpha, charlie, deletedShift, foreign, unlinkedByDeletion);

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _clientId, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = bravo.Id, GroupId = _rootId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = bravo.Id, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = alpha.Id, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = charlie.Id, GroupId = _rootId, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = deletedShift.Id, GroupId = _rootId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = foreign.Id, GroupId = _foreignGroupId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = unlinkedByDeletion.Id, GroupId = _rootId, IsDeleted = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _handler.Handle(new GetAvailableShiftsQuery(_clientId), CancellationToken.None);

        result.Select(s => s.Name).ShouldBe(["alpha", "bravo", "charlie"]);
        result.Select(s => s.Abbreviation).ShouldBe(["A", "B", "C"]);
        result.Select(s => s.Id).ShouldBe([alpha.Id, bravo.Id, charlie.Id]);
    }

    [Test]
    public async Task ClientWithoutGroups_GetsAnEmptyList()
    {
        _context.Group.Add(new Group { Id = _rootId, Name = "root" });
        var shift = Shift("alpha", "A");
        _context.Shift.Add(shift);
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = shift.Id, GroupId = _rootId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _handler.Handle(new GetAvailableShiftsQuery(_clientId), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    private static Shift Shift(string name, string abbreviation) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Abbreviation = abbreviation,
        Status = ShiftStatus.OriginalShift,
    };
}
