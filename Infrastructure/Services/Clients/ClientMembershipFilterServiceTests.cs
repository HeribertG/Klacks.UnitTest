// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the membership boundaries of ClientMembershipFilterService against the company date: the last validity day is
/// inclusive (ValidUntil = today is still active, former starts the day after), ValidFrom = today is already active and
/// ValidFrom = tomorrow is future, for the single-state checks and for every flag combination of the query filter.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Clients;

[TestFixture]
public class ClientMembershipFilterServiceTests
{
    private const string UntilToday = "UntilToday";
    private const string UntilYesterday = "UntilYesterday";
    private const string FromToday = "FromToday";
    private const string FromTomorrow = "FromTomorrow";
    private const string OpenEnded = "OpenEnded";

    private static readonly DateTime Today = Utc(2026, 11, 14);
    private static readonly DateTime Yesterday = Today.AddDays(-1);
    private static readonly DateTime Tomorrow = Today.AddDays(1);
    private static readonly DateTime LongAgo = Utc(2020, 1, 1);

    private DataBaseContext _dbContext = null!;
    private ClientMembershipFilterService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var afternoonOfToday = new DateTimeOffset(2026, 11, 14, 15, 30, 0, TimeSpan.Zero);
        _service = new ClientMembershipFilterService(new FixedCompanyClock(afternoonOfToday));

        Seed(UntilToday, LongAgo, Today);
        Seed(UntilYesterday, LongAgo, Yesterday);
        Seed(FromToday, Today, null);
        Seed(FromTomorrow, Tomorrow, null);
        Seed(OpenEnded, LongAgo, null);
        _dbContext.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    [Test]
    public async Task IsActiveMembership_IncludesTheLastValidityDayAndTheFirstOne()
    {
        (await _service.IsActiveMembership(LongAgo, Today)).ShouldBeTrue();
        (await _service.IsActiveMembership(Today, null)).ShouldBeTrue();
        (await _service.IsActiveMembership(LongAgo, Yesterday)).ShouldBeFalse();
        (await _service.IsActiveMembership(Tomorrow, null)).ShouldBeFalse();
    }

    [Test]
    public async Task IsFormerMembership_StartsTheDayAfterValidUntil()
    {
        (await _service.IsFormerMembership(LongAgo, Today)).ShouldBeFalse();
        (await _service.IsFormerMembership(LongAgo, Yesterday)).ShouldBeTrue();
        (await _service.IsFormerMembership(LongAgo, null)).ShouldBeFalse();
    }

    [Test]
    public async Task IsFutureMembership_StartsTheDayAfterToday()
    {
        (await _service.IsFutureMembership(Today, null)).ShouldBeFalse();
        (await _service.IsFutureMembership(Tomorrow, null)).ShouldBeTrue();
    }

    [TestCase(true, false, false, new[] { UntilToday, FromToday, OpenEnded })]
    [TestCase(false, true, false, new[] { UntilYesterday })]
    [TestCase(false, false, true, new[] { FromTomorrow })]
    [TestCase(true, true, false, new[] { UntilToday, FromToday, OpenEnded, UntilYesterday })]
    [TestCase(true, false, true, new[] { UntilToday, FromToday, OpenEnded, FromTomorrow })]
    [TestCase(false, true, true, new[] { UntilYesterday, FromTomorrow })]
    [TestCase(true, true, true, new[] { UntilToday, UntilYesterday, FromToday, FromTomorrow, OpenEnded })]
    public async Task ApplyMembershipFilter_KeepsExactlyTheClientsOfTheRequestedStates(
        bool active, bool former, bool future, string[] expected)
    {
        var query = _dbContext.Client.Include(c => c.Membership).AsNoTracking();

        var filtered = await _service.ApplyMembershipFilter(query, active, former, future);

        (await filtered.Select(c => c.Name).ToListAsync()).ShouldBe(expected, ignoreOrder: true);
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private void Seed(string name, DateTime validFrom, DateTime? validUntil)
    {
        var id = Guid.NewGuid();
        _dbContext.Client.Add(new Client
        {
            Id = id,
            Name = name,
            FirstName = "First",
            Membership = new Membership { Id = Guid.NewGuid(), ClientId = id, ValidFrom = validFrom, ValidUntil = validUntil }
        });
    }
}
