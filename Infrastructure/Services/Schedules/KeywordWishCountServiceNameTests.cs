// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The missed wishes of <see cref="KeywordWishCountService.CountAsync"/> carry the employee's display name, read in one
/// batch for the employees with a missed wish; an employee without a name is shown as the id text, as on the soft
/// alternative's broken wishes.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public sealed class KeywordWishCountServiceNameTests
{
    private const string FreeKeyword = "FREE";
    private const string AnnaName = "Anna Muster";

    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Unnamed = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 11, 3);

    private IScheduleCommandRepository _commands = null!;
    private IClientRepository _clients = null!;
    private DataBaseContext _context = null!;
    private KeywordWishCountService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _commands = Substitute.For<IScheduleCommandRepository>();
        _clients = Substitute.For<IClientRepository>();
        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);
        var options = new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new KeywordWishCountService(_context, _commands, keywordProvider, _clients);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task CountAsync_NamesEveryMissedWish_AndFallsBackToTheIdText()
    {
        _commands.GetByClientsAndDateRangeAsync(Arg.Any<IReadOnlyList<Guid>>(), Day, Day, null, Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleCommand>
            {
                new() { ClientId = Anna, CurrentDate = Day, CommandKeyword = FreeKeyword },
                new() { ClientId = Unnamed, CurrentDate = Day, CommandKeyword = FreeKeyword },
            });
        _clients.GetDisplayNamesAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(Anna) && ids.Contains(Unnamed)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [Anna] = AnnaName });
        _context.Work.AddRange(EarlyWork(Anna), EarlyWork(Unnamed));
        await _context.SaveChangesAsync();

        var wishes = await _sut.CountAsync([Anna, Unnamed], Day, Day, analyseToken: null, CancellationToken.None);

        wishes.BrokenFree.ShouldBe(2);
        wishes.Items.Single(i => i.ClientId == Anna).ClientName.ShouldBe(AnnaName);
        wishes.Items.Single(i => i.ClientId == Unnamed).ClientName.ShouldBe(Unnamed.ToString());
        await _clients.Received(1).GetDisplayNamesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CountAsync_WithoutMissedWish_ReadsNoName()
    {
        _commands.GetByClientsAndDateRangeAsync(Arg.Any<IReadOnlyList<Guid>>(), Day, Day, null, Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleCommand> { new() { ClientId = Anna, CurrentDate = Day, CommandKeyword = FreeKeyword } });

        var wishes = await _sut.CountAsync([Anna], Day, Day, analyseToken: null, CancellationToken.None);

        wishes.Items.ShouldBeEmpty();
        await _clients.DidNotReceiveWithAnyArgs().GetDisplayNamesAsync(default!, default);
    }

    private static Work EarlyWork(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = Day,
        StartTime = new TimeOnly(7, 0),
        EndTime = new TimeOnly(15, 0),
        WorkTime = 8m,
    };
}
