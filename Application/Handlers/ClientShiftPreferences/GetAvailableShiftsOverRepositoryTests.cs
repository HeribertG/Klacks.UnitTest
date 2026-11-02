// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes GetAvailableShiftsQueryHandler over the real GroupItemRepository (EF InMemory): the shifts linked
/// to the groups of the client's group tree that the caller may see, each shift once, ordered by name; deleted links,
/// deleted shifts, shift-less items, groups outside the tree and every row of a variant or scenario contribute nothing.
/// </summary>

using Klacks.Api.Application.Handlers.ClientShiftPreferences;
using Klacks.Api.Application.Queries.ClientShiftPreferences;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ClientShiftPreferences;

[TestFixture]
public class GetAvailableShiftsOverRepositoryTests
{
    private DataBaseContext _context = null!;
    private GetAvailableShiftsQueryHandler _handler = null!;
    private IClientVisibilityGuard _clientGuard = null!;
    private GroupItemRepository _repository = null!;

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

        _clientGuard = Substitute.For<IClientVisibilityGuard>();
        _clientGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _repository = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());

        _handler = NewHandler(ShiftVisibilityGuards.AllowAll());
    }

    private GetAvailableShiftsQueryHandler NewHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, _clientGuard, shiftGuard, Substitute.For<ILogger<GetAvailableShiftsQueryHandler>>());

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
        var keyShift = Shift("charlie", "C");
        keyShift.AnalyseToken = Guid.NewGuid();
        var scenarioClone = Shift("golf", "G");
        scenarioClone.AnalyseToken = Guid.NewGuid();
        scenarioClone.ScenarioSourceShiftId = bravo.Id;
        var tokenedLinkOnly = Shift("hotel", "H");
        var deletedShift = Shift("delta", "D");
        deletedShift.IsDeleted = true;
        var foreign = Shift("echo", "E");
        var unlinkedByDeletion = Shift("foxtrot", "F");
        _context.Shift.AddRange(bravo, alpha, keyShift, scenarioClone, tokenedLinkOnly, deletedShift, foreign, unlinkedByDeletion);

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _clientId, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = bravo.Id, GroupId = _rootId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = bravo.Id, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = alpha.Id, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = keyShift.Id, GroupId = _rootId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = scenarioClone.Id, GroupId = _rootId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = tokenedLinkOnly.Id, GroupId = _rootId, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = deletedShift.Id, GroupId = _rootId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = foreign.Id, GroupId = _foreignGroupId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = unlinkedByDeletion.Id, GroupId = _rootId, IsDeleted = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _handler.Handle(new GetAvailableShiftsQuery(_clientId), CancellationToken.None);

        result.Select(s => s.Name).ShouldBe(["alpha", "bravo"]);
        result.Select(s => s.Abbreviation).ShouldBe(["A", "B"]);
        result.Select(s => s.Id).ShouldBe([alpha.Id, bravo.Id]);
    }

    [Test]
    public async Task AShiftTheCallerMayNotSee_IsLeftOut_LikeOneThatDoesNotExist()
    {
        _context.Group.AddRange(
            new Group { Id = _rootId, Name = "root" },
            new Group { Id = _childId, Name = "child", Root = _rootId, Parent = _rootId },
            new Group { Id = _foreignGroupId, Name = "sister", Root = _rootId, Parent = _rootId });
        var visible = Shift("alpha", "A");
        var hidden = Shift("bravo", "B");
        _context.Shift.AddRange(visible, hidden);
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _clientId, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = visible.Id, GroupId = _childId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = hidden.Id, GroupId = _foreignGroupId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await NewHandler(ShiftVisibilityGuards.Hiding(hidden.Id))
            .Handle(new GetAvailableShiftsQuery(_clientId), CancellationToken.None);

        result.Select(s => s.Id).ShouldBe([visible.Id]);
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
