// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Filter semantics of the AnalyseScenarioRepository queries that moved from in-memory filtering into the
/// query: GetActiveCreatedBetweenAsync (ScenarioPendingDetector) returns only Active, not deleted scenarios
/// inside the create-time window, and ListVisibleAsync (list_scenarios) honours group, onlyOpen and the
/// visible roots. Runs on the EF InMemory provider, so it proves the semantics, not the Npgsql SQL
/// translation. CreateTime is stamped by the context on insert, so the window is placed around "now".
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class AnalyseScenarioRepositoryQueryTests
{
    private DataBaseContext _context = null!;
    private AnalyseScenarioRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private static AnalyseScenario Scenario(string name, AnalyseScenarioStatus status, Guid? groupId = null, bool deleted = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Token = Guid.NewGuid(),
        Status = status,
        GroupId = groupId,
        IsDeleted = deleted
    };

    private async Task SeedAsync(params object[] entities)
    {
        _context.AddRange(entities);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [Test]
    public async Task GetActiveCreatedBetweenAsync_ReturnsOnlyActiveNotDeletedInsideTheWindow()
    {
        await SeedAsync(
            Scenario("active", AnalyseScenarioStatus.Active),
            Scenario("accepted", AnalyseScenarioStatus.Accepted),
            Scenario("rejected", AnalyseScenarioStatus.Rejected),
            Scenario("deleted", AnalyseScenarioStatus.Active, deleted: true));
        var now = DateTime.UtcNow;

        var inside = await _repository.GetActiveCreatedBetweenAsync(now.AddHours(-1), now.AddHours(1));
        var tooYoung = await _repository.GetActiveCreatedBetweenAsync(now.AddDays(-30), now.AddHours(-48));

        inside.Select(s => s.Name).ShouldBe(new[] { "active" });
        tooYoung.ShouldBeEmpty();
    }

    [Test]
    public async Task ListVisibleAsync_AppliesGroupOnlyOpenAndVisibleRoots()
    {
        var root = new Group { Id = Guid.NewGuid(), Name = "Winterthur" };
        var sub = new Group { Id = Guid.NewGuid(), Name = "Winterthur Nord", Parent = root.Id, Root = root.Id };
        var other = new Group { Id = Guid.NewGuid(), Name = "Bern" };
        await SeedAsync(
            root, sub, other,
            Scenario("root open", AnalyseScenarioStatus.Active, root.Id),
            Scenario("root done", AnalyseScenarioStatus.Accepted, root.Id),
            Scenario("sub open", AnalyseScenarioStatus.Active, sub.Id),
            Scenario("other open", AnalyseScenarioStatus.Active, other.Id),
            Scenario("groupless open", AnalyseScenarioStatus.Active),
            Scenario("root deleted", AnalyseScenarioStatus.Active, root.Id, deleted: true));

        var all = await _repository.ListVisibleAsync(null, onlyOpen: false, visibleRootIds: null);
        var open = await _repository.ListVisibleAsync(null, onlyOpen: true, visibleRootIds: null);
        var rootOnly = await _repository.ListVisibleAsync(root.Id, onlyOpen: false, visibleRootIds: null);
        var restricted = await _repository.ListVisibleAsync(null, onlyOpen: true, visibleRootIds: [root.Id]);

        all.Select(s => s.Name).ShouldBe(
            new[] { "root open", "root done", "sub open", "other open", "groupless open" }, ignoreOrder: true);
        open.Select(s => s.Name).ShouldBe(
            new[] { "root open", "sub open", "other open", "groupless open" }, ignoreOrder: true);
        rootOnly.Select(s => s.Name).ShouldBe(new[] { "root open", "root done" }, ignoreOrder: true);
        restricted.Select(s => s.Name).ShouldBe(new[] { "root open", "sub open" }, ignoreOrder: true);
        restricted.ShouldAllBe(s => s.Group != null);
    }
}
