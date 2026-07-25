// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupItemRepository.GetByClientAndGroup: it returns only the real membership of a
/// client in a group. A scenario membership (AnalyseToken set) may coexist with the real one for the
/// same client and group, and must never be handed out — callers soft-delete or count what they get,
/// which would destroy a running analysis scenario and skip the real change.
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
}
