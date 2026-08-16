// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for EscalationRosterService.GetRosterEntriesAsync and SetOrderAsync: active entries sort by
/// EffectiveRank with orphaned entries trailing by OverrideRank, an unresolvable user id falls back to
/// showing the raw id instead of crashing, SetOrderAsync persists OverrideRank in submission order and
/// silently skips a user id with no roster row - and, most importantly, SetOrderAsync must never flip
/// IsOrphaned or touch OverrideRank for an entry RederiveAsync has just marked orphaned, even if that
/// entry's user id is present in the submitted order (E60 safety rule).
/// </summary>

using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Services.Assistant.Escalation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Escalation;

[TestFixture]
public class EscalationRosterServiceTests
{
    private string _databaseName = null!;
    private DataBaseContext _dbContext = null!;
    private IGroupRepository _groupRepository = null!;
    private UserManager<AppUser> _userManager = null!;
    private EscalationRosterService _service = null!;

    [Test]
    public async Task GetRosterEntriesAsync_OrdersActiveEntriesByEffectiveRank_ThenOrphanedByOverrideRank()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        var userA = StubUser("user-a", "Anna", "Adler");
        var userB = StubUser("user-b", "Bruno", "Berger");
        var userC = StubUser("user-c", "Clara", "Costa");
        var userD = StubUser("user-d", "Diego", "Diaz");

        _dbContext.GroupVisibility.AddRange(
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userA.Id, GroupId = groupId },
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userB.Id, GroupId = groupId },
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userC.Id, GroupId = groupId });

        _dbContext.EscalationRosterEntries.AddRange(
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userA.Id, OverrideRank = 3, DerivedRank = 1 },
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userB.Id, OverrideRank = 1, DerivedRank = 2 },
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userC.Id, OverrideRank = 2, DerivedRank = 3 },
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userD.Id, OverrideRank = 9 });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterEntriesAsync(groupId);

        result.Count.ShouldBe(4);
        result[0].UserId.ShouldBe(userB.Id);
        result[0].DisplayName.ShouldBe("Bruno Berger");
        result[0].IsOrphaned.ShouldBeFalse();
        result[1].UserId.ShouldBe(userC.Id);
        result[2].UserId.ShouldBe(userA.Id);
        result[3].UserId.ShouldBe(userD.Id);
        result[3].IsOrphaned.ShouldBeTrue();
        result[3].EffectiveRank.ShouldBe(9);
    }

    [Test]
    public async Task GetRosterEntriesAsync_UnknownUser_FallsBackToUserId()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        const string unknownUserId = "user-unknown";
        _userManager.FindByIdAsync(unknownUserId).Returns(Task.FromResult<AppUser?>(null));

        _dbContext.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = unknownUserId, GroupId = groupId });
        _dbContext.EscalationRosterEntries.Add(new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = unknownUserId, OverrideRank = 1 });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterEntriesAsync(groupId);

        result.Count.ShouldBe(1);
        result[0].DisplayName.ShouldBe(unknownUserId);
    }

    [Test]
    public async Task SetOrderAsync_SetsOverrideRankInSubmittedOrder()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        const string userA = "user-a";
        const string userB = "user-b";
        const string userC = "user-c";

        _dbContext.GroupVisibility.AddRange(
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userA, GroupId = groupId },
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userB, GroupId = groupId },
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userC, GroupId = groupId });
        _dbContext.EscalationRosterEntries.AddRange(
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userA, OverrideRank = 1 },
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userB, OverrideRank = 2 },
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userC, OverrideRank = 3 });
        await _dbContext.SaveChangesAsync();

        await _service.SetOrderAsync(groupId, new[] { userC, userA, userB });

        using var verifyContext = CreateContext();
        var entries = await verifyContext.EscalationRosterEntries
            .Where(e => e.GroupRootId == groupId)
            .ToDictionaryAsync(e => e.UserId);

        entries[userC].OverrideRank.ShouldBe(1);
        entries[userA].OverrideRank.ShouldBe(2);
        entries[userB].OverrideRank.ShouldBe(3);
    }

    [Test]
    public async Task SetOrderAsync_UnknownUserIdInList_IsSilentlyIgnored()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        const string userA = "user-a";
        const string userB = "user-b";
        const string ghostUserId = "user-ghost";

        _dbContext.GroupVisibility.AddRange(
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userA, GroupId = groupId },
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userB, GroupId = groupId });
        _dbContext.EscalationRosterEntries.AddRange(
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userA, OverrideRank = 5 },
            new EscalationRosterEntry { Id = Guid.NewGuid(), GroupRootId = groupId, UserId = userB, OverrideRank = 6 });
        await _dbContext.SaveChangesAsync();

        await _service.SetOrderAsync(groupId, new[] { ghostUserId, userA, userB });

        using var verifyContext = CreateContext();
        var entries = await verifyContext.EscalationRosterEntries
            .Where(e => e.GroupRootId == groupId)
            .ToDictionaryAsync(e => e.UserId);

        entries.Count.ShouldBe(2);
        entries[userA].OverrideRank.ShouldBe(2);
        entries[userB].OverrideRank.ShouldBe(3);
    }

    [Test]
    public async Task SetOrderAsync_OrphanedEntryInList_IsNeverUnorphaned()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        const string orphanedUserId = "user-x";
        const int priorOverrideRank = 7;

        _dbContext.EscalationRosterEntries.Add(new EscalationRosterEntry
        {
            Id = Guid.NewGuid(),
            GroupRootId = groupId,
            UserId = orphanedUserId,
            OverrideRank = priorOverrideRank
        });
        await _dbContext.SaveChangesAsync();

        await _service.SetOrderAsync(groupId, new[] { orphanedUserId });

        using var verifyContext = CreateContext();
        var entry = await verifyContext.EscalationRosterEntries
            .SingleAsync(e => e.GroupRootId == groupId && e.UserId == orphanedUserId);

        entry.IsOrphaned.ShouldBeTrue();
        entry.OverrideRank.ShouldBe(priorOverrideRank);
    }

    [SetUp]
    public void Setup()
    {
        _databaseName = Guid.NewGuid().ToString();
        _dbContext = CreateContext();
        _dbContext.Database.EnsureCreated();

        _groupRepository = Substitute.For<IGroupRepository>();

        var userStore = Substitute.For<IUserStore<AppUser>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errors = Substitute.For<IdentityErrorDescriber>();
        var services = Substitute.For<IServiceProvider>();
        var userManagerLogger = Substitute.For<ILogger<UserManager<AppUser>>>();

        _userManager = Substitute.For<UserManager<AppUser>>(
            userStore, identityOptions, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errors, services, userManagerLogger);

        var serviceLogger = Substitute.For<ILogger<EscalationRosterService>>();

        _service = new EscalationRosterService(_dbContext, _groupRepository, _userManager, serviceLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
        _userManager?.Dispose();
    }

    private DataBaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: _databaseName)
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private AppUser StubUser(string id, string firstName, string lastName)
    {
        var user = new AppUser { Id = id, FirstName = firstName, LastName = lastName, UserName = $"{id}@test.local" };
        _userManager.FindByIdAsync(id).Returns(Task.FromResult<AppUser?>(user));
        return user;
    }
}
