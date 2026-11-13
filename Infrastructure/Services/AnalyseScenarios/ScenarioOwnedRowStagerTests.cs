// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioOwnedRowStager on an in-memory DataBaseContext: a temporary membership is one row per day. Adding Monday and
/// Friday writes two day rows and nothing for the days between, the same day is not doubled (saved or still unsaved),
/// removing a day leaves the other days, and a row over several days (written before memberships were kept per day) is
/// read as covering its days and is cut into day rows when one of them is removed. On accept the day rows become
/// temporary memberships of the real plan, except the days a real membership of the person in that group already
/// covers. The in-memory provider enforces no unique index; the index side is pinned by the integration test
/// TemporaryMembershipPerDayTests.
/// </summary>

using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class ScenarioOwnedRowStagerTests
{
    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);
    private static readonly DateOnly Thursday = Monday.AddDays(3);
    private static readonly DateOnly Friday = Monday.AddDays(4);
    private static readonly DateTime Now = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _token = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _groupId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private ScenarioOwnedRowStager _stager = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _stager = new ScenarioOwnedRowStager(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task AddOnMondayAndOnFriday_WritesTwoDayRows_AndNothingForTheDaysBetween()
    {
        // Act
        await AddAsync(Monday, Monday);
        await AddAsync(Friday, Friday);
        await _context.SaveChangesAsync();

        // Assert
        var memberships = await ScenarioMembershipsAsync();
        DaysOf(memberships).ShouldBe(new[] { (Monday, Monday), (Friday, Friday) });
        memberships.ShouldAllBe(membership => membership.IsTemporary);
        memberships.ShouldAllBe(membership => membership.ValidFrom!.Value.Kind == DateTimeKind.Utc);
    }

    [Test]
    public async Task AddTheSameDayTwice_BeforeSaving_WritesOneRow()
    {
        // Act
        await AddAsync(Monday, Monday);
        await AddAsync(Monday, Monday);
        await _context.SaveChangesAsync();

        // Assert
        DaysOf(await ScenarioMembershipsAsync()).ShouldBe(new[] { (Monday, Monday) });
    }

    [Test]
    public async Task AddTheSameDayAgain_AfterSaving_WritesNoSecondRow()
    {
        // Arrange
        await AddAsync(Monday, Monday);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await AddAsync(Monday, Monday);
        await _context.SaveChangesAsync();

        // Assert
        DaysOf(await ScenarioMembershipsAsync()).ShouldBe(new[] { (Monday, Monday) });
    }

    [Test]
    public async Task AddAWindowOfSeveralDays_WritesOneRowPerDay_AndSkipsTheDayAlreadyThere()
    {
        // Arrange
        await AddAsync(Tuesday, Tuesday);
        await _context.SaveChangesAsync();

        // Act
        await AddAsync(Monday, Wednesday);
        await _context.SaveChangesAsync();

        // Assert
        DaysOf(await ScenarioMembershipsAsync()).ShouldBe(new[] { (Monday, Monday), (Tuesday, Tuesday), (Wednesday, Wednesday) });
    }

    [Test]
    public async Task AddADay_InsideASavedRowOverSeveralDays_WritesNoRow()
    {
        // Arrange: a row from before memberships were kept per day.
        SeedMembership(Monday, Friday, _token);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await AddAsync(Wednesday, Wednesday);
        await _context.SaveChangesAsync();

        // Assert
        DaysOf(await ScenarioMembershipsAsync()).ShouldBe(new[] { (Monday, Friday) });
    }

    [Test]
    public async Task AddADay_ThatAnotherGroupOrAnotherScenarioHolds_WritesItsOwnRow()
    {
        // Arrange
        SeedMembership(Monday, Monday, _token, groupId: Guid.NewGuid());
        SeedMembership(Monday, Monday, Guid.NewGuid());
        await _context.SaveChangesAsync();

        // Act
        await AddAsync(Monday, Monday);
        await _context.SaveChangesAsync();

        // Assert
        DaysOf(await ScenarioMembershipsAsync()).ShouldBe(new[] { (Monday, Monday) });
    }

    [Test]
    public async Task RemoveMonday_LeavesFriday()
    {
        // Arrange
        await AddAsync(Monday, Monday);
        await AddAsync(Friday, Friday);
        await _context.SaveChangesAsync();

        // Act
        await _stager.RemoveScenarioMembershipsAsync(_token, _clientId, Monday, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        DaysOf(await ScenarioMembershipsAsync()).ShouldBe(new[] { (Friday, Friday) });
    }

    [Test]
    public async Task RemoveADay_ThatIsStagedButNotSaved_DropsIt()
    {
        // Arrange
        await AddAsync(Monday, Monday);

        // Act
        await _stager.RemoveScenarioMembershipsAsync(_token, _clientId, Monday, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        (await _context.Set<GroupItem>().IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task RemoveADay_FromARowOverSeveralDays_KeepsItsOtherDaysAsDayRows()
    {
        // Arrange: a row from before memberships were kept per day.
        var legacy = SeedMembership(Monday, Friday, _token);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _stager.RemoveScenarioMembershipsAsync(_token, _clientId, Wednesday, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var memberships = await ScenarioMembershipsAsync();
        DaysOf(memberships).ShouldBe(new[] { (Monday, Monday), (Tuesday, Tuesday), (Thursday, Thursday), (Friday, Friday) });
        memberships.ShouldAllBe(membership => membership.IsTemporary);
        memberships[0].Id.ShouldBe(legacy.Id, "the row itself shrinks to its first remaining day");
    }

    [Test]
    public async Task RemoveADay_TouchesNeitherAnotherClientNorAnotherScenarioNorAnotherDay()
    {
        // Arrange
        var otherClient = SeedMembership(Monday, Monday, _token, clientId: Guid.NewGuid());
        var otherScenario = SeedMembership(Monday, Monday, Guid.NewGuid());
        var otherDay = SeedMembership(Tuesday, Tuesday, _token);
        await _context.SaveChangesAsync();

        // Act
        await _stager.RemoveScenarioMembershipsAsync(_token, _clientId, Monday, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var live = await _context.Set<GroupItem>().AsNoTracking().Select(gi => gi.Id).ToListAsync();
        live.ShouldBe([otherClient.Id, otherScenario.Id, otherDay.Id], ignoreOrder: true);
    }

    [Test]
    public async Task Promote_TwoDayRows_BecomeTwoTemporaryMembershipsOfTheRealPlan()
    {
        // Arrange
        await AddAsync(Monday, Monday);
        await AddAsync(Friday, Friday);
        await _context.SaveChangesAsync();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var real = await RealMembershipsAsync();
        DaysOf(real).ShouldBe(new[] { (Monday, Monday), (Friday, Friday) });
        real.ShouldAllBe(membership => membership.IsTemporary);
        (await ScenarioMembershipsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Promote_ADayARegularMembershipAlreadyCovers_IsDiscarded_TheOtherDayIsTakenOver()
    {
        // Arrange: the person became a regular member that ends on Monday.
        var regular = SeedMembership(Monday.AddDays(-30), Monday, token: null, isTemporary: false);
        await AddAsync(Monday, Monday);
        await AddAsync(Friday, Friday);
        await _context.SaveChangesAsync();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var real = await RealMembershipsAsync();
        real.Select(membership => membership.Id).ShouldContain(regular.Id);
        DaysOf(real.Where(membership => membership.IsTemporary).ToList()).ShouldBe(new[] { (Friday, Friday) });
        var discarded = (await DeletedScenarioMembershipsAsync()).ShouldHaveSingleItem();
        discarded.ValidFrom.ShouldBe(Utc(Monday));
        discarded.DeletedTime.ShouldBe(Now);
    }

    [Test]
    public async Task Promote_ADayAnotherScenarioAlreadyTookOver_IsDiscarded_NotDoubled()
    {
        // Arrange: a parallel scenario borrowed the same person for the same day and was accepted first.
        var takenOver = SeedMembership(Monday, Monday, token: null);
        await AddAsync(Monday, Monday);
        await _context.SaveChangesAsync();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        (await RealMembershipsAsync()).ShouldHaveSingleItem().Id.ShouldBe(takenOver.Id);
        (await DeletedScenarioMembershipsAsync()).ShouldHaveSingleItem();
    }

    [Test]
    public async Task Promote_ARowOverSeveralDays_ThatIsPartlyCovered_KeepsOnlyItsUncoveredDays()
    {
        // Arrange: a row from before memberships were kept per day, and Wednesday already taken over.
        SeedMembership(Wednesday, Wednesday, token: null);
        SeedMembership(Monday, Friday, _token);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var real = await RealMembershipsAsync();
        DaysOf(real).ShouldBe(
            new[] { (Monday, Monday), (Tuesday, Tuesday), (Wednesday, Wednesday), (Thursday, Thursday), (Friday, Friday) });
        real.ShouldAllBe(membership => membership.IsTemporary);
    }

    [Test]
    public async Task Promote_ARowOverSeveralDays_ThatNothingCovers_IsTakenOverAsItIs()
    {
        // Arrange
        var legacy = SeedMembership(Monday, Friday, _token);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var real = (await RealMembershipsAsync()).ShouldHaveSingleItem();
        real.Id.ShouldBe(legacy.Id);
        DaysOf(new List<GroupItem> { real }).ShouldBe(new[] { (Monday, Friday) });
        real.IsTemporary.ShouldBeTrue();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task Promote_ARowWithoutAFirstOrLastDay_BecomesARegularMembership_NotATemporaryOne(bool withoutFrom, bool withoutUntil)
    {
        // Arrange: no day row; as a temporary row it would be outside the day index and outside every period.
        var open = SeedMembership(Monday, Friday, _token);
        open.ValidFrom = withoutFrom ? null : open.ValidFrom;
        open.ValidUntil = withoutUntil ? null : open.ValidUntil;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var real = (await RealMembershipsAsync()).ShouldHaveSingleItem();
        real.Id.ShouldBe(open.Id);
        real.IsTemporary.ShouldBeFalse();
    }

    [Test]
    public async Task Promote_ARowWithoutAFirstDay_IsDiscarded_WhenTheClientAlreadyIsARegularMember()
    {
        // Arrange
        var regular = SeedMembership(Monday.AddDays(-30), Monday.AddDays(30), token: null, isTemporary: false);
        var open = SeedMembership(Monday, Friday, _token);
        open.ValidFrom = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        (await RealMembershipsAsync()).ShouldHaveSingleItem().Id.ShouldBe(regular.Id);
        (await DeletedScenarioMembershipsAsync()).ShouldHaveSingleItem().Id.ShouldBe(open.Id);
    }

    [Test]
    public async Task Promote_DiscardsTheShiftLinksOfTheScenario_AndLeavesAnotherScenarioAlone()
    {
        // Arrange
        var shiftLink = new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = Guid.NewGuid(), AnalyseToken = _token };
        _context.Set<GroupItem>().Add(shiftLink);
        var otherScenario = SeedMembership(Monday, Monday, Guid.NewGuid());
        await _context.SaveChangesAsync();

        // Act
        await _stager.PromoteScenarioMembershipsAsync(_token, Now, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Assert
        var stored = await _context.Set<GroupItem>().IgnoreQueryFilters().AsNoTracking().ToListAsync();
        stored.Single(gi => gi.Id == shiftLink.Id).IsDeleted.ShouldBeTrue();
        var untouched = stored.Single(gi => gi.Id == otherScenario.Id);
        untouched.IsDeleted.ShouldBeFalse();
        untouched.AnalyseToken.ShouldBe(otherScenario.AnalyseToken);
    }

    private Task AddAsync(DateOnly from, DateOnly until)
        => _stager.AddScenarioMembershipAsync(_token, _clientId, _groupId, from, until, CancellationToken.None);

    private GroupItem SeedMembership(
        DateOnly from, DateOnly until, Guid? token, Guid? clientId = null, Guid? groupId = null, bool isTemporary = true)
    {
        var membership = new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId ?? _clientId,
            GroupId = groupId ?? _groupId,
            ValidFrom = Utc(from),
            ValidUntil = Utc(until),
            AnalyseToken = token,
            IsTemporary = isTemporary,
        };
        _context.Set<GroupItem>().Add(membership);
        return membership;
    }

    private Task<List<GroupItem>> ScenarioMembershipsAsync()
        => _context.Set<GroupItem>().AsNoTracking()
            .Where(gi => gi.AnalyseToken == _token && gi.ClientId == _clientId && gi.GroupId == _groupId)
            .OrderBy(gi => gi.ValidFrom)
            .ToListAsync();

    private Task<List<GroupItem>> DeletedScenarioMembershipsAsync()
        => _context.Set<GroupItem>().IgnoreQueryFilters().AsNoTracking()
            .Where(gi => gi.AnalyseToken == _token && gi.ClientId == _clientId && gi.IsDeleted)
            .ToListAsync();

    private Task<List<GroupItem>> RealMembershipsAsync()
        => _context.Set<GroupItem>().AsNoTracking()
            .Where(gi => gi.AnalyseToken == null && gi.ClientId == _clientId && gi.GroupId == _groupId)
            .OrderBy(gi => gi.ValidFrom)
            .ToListAsync();

    private static List<(DateOnly From, DateOnly Until)> DaysOf(List<GroupItem> memberships)
        => memberships
            .Select(membership => (DateOnly.FromDateTime(membership.ValidFrom!.Value), DateOnly.FromDateTime(membership.ValidUntil!.Value)))
            .ToList();

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
