// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the visibility-preservation hook in GroupRepository.Add - the one shared creation
/// path every controller, command handler and group-creating skill funnels through. Verifies that a
/// root group triggers the grant, a child group never does, and that the granted rows are staged
/// before the repository's SaveChanges so they are committed together with the group itself.
/// </summary>

using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Groups;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class GroupRepositoryVisibilityPreservationTests
{
    private const string PlannerUserId = "planner-user-id";

    private DataBaseContext _context = null!;
    private IGroupVisibilityPreservationService _visibilityPreservation = null!;
    private GroupRepository _groupRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var treeService = Substitute.For<IGroupTreeService>();
        treeService.AddRootNodeAsync(Arg.Any<Group>()).Returns(call =>
        {
            var group = call.ArgAt<Group>(0);
            group.Parent = null;
            group.Root = null;
            _context.Group.Add(group);
            return Task.FromResult(group);
        });
        treeService.AddChildNodeAsync(Arg.Any<Guid>(), Arg.Any<Group>()).Returns(call =>
        {
            var group = call.ArgAt<Group>(1);
            _context.Group.Add(group);
            return Task.FromResult(group);
        });

        var facade = Substitute.For<IGroupServiceFacade>();
        facade.TreeService.Returns(treeService);

        _visibilityPreservation = Substitute.For<IGroupVisibilityPreservationService>();

        _groupRepository = new GroupRepository(
            _context,
            facade,
            Substitute.For<IGroupCacheService>(),
            Substitute.For<ILogger<Group>>(),
            new FixedCompanyClock(DateTimeOffset.UtcNow),
            _visibilityPreservation);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task Add_RootGroup_TriggersPreservation_WhenTheServiceAsksForIt()
    {
        var root = NewGroup("Bern");
        _visibilityPreservation.RequiresPreservationAsync(root, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await _groupRepository.Add(root);

        await _visibilityPreservation.Received(1).PreserveForNewRootAsync(root, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Add_DoesNotTriggerPreservation_WhenTheServiceDeclines()
    {
        var root = NewGroup("Bern");
        _visibilityPreservation.RequiresPreservationAsync(root, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        await _groupRepository.Add(root);

        await _visibilityPreservation.DidNotReceive()
            .PreserveForNewRootAsync(Arg.Any<Group>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Add_ChildGroup_IsAskedWithTheChild_AndNeverPreserves()
    {
        var parent = NewGroup("Bern");
        _visibilityPreservation.RequiresPreservationAsync(Arg.Any<Group>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        await _groupRepository.Add(parent);

        var child = NewGroup("Bern Nord");
        child.Parent = parent.Id;

        await _groupRepository.Add(child);

        await _visibilityPreservation.Received(1).RequiresPreservationAsync(child, Arg.Any<CancellationToken>());
        await _visibilityPreservation.DidNotReceive()
            .PreserveForNewRootAsync(Arg.Any<Group>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Add_CommitsTheGrantedRows_TogetherWithTheGroup()
    {
        var root = NewGroup("Bern");
        _visibilityPreservation.RequiresPreservationAsync(root, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _visibilityPreservation.PreserveForNewRootAsync(root, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _context.GroupVisibility.Add(new GroupVisibility
                {
                    Id = Guid.NewGuid(),
                    AppUserId = PlannerUserId,
                    Group = root
                });
                return Task.FromResult(1);
            });

        await _groupRepository.Add(root);

        _context.ChangeTracker.Clear();
        var rows = await _context.GroupVisibility.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1, "the staged row must be part of the repository's own SaveChanges");
        rows[0].AppUserId.ShouldBe(PlannerUserId);
        rows[0].GroupId.ShouldBe(root.Id);
        (await _context.Group.AsNoTracking().AnyAsync(g => g.Id == root.Id)).ShouldBeTrue();
    }

    private static Group NewGroup(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ValidFrom = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc)
    };
}
