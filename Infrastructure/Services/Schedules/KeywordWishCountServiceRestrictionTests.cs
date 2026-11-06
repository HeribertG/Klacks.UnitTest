// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The soft-retry gate of <see cref="KeywordWishCountService.HasRestrictingWishesAsync"/>: only a command that restricts
/// the day (FREE, OnlyX, NoX, also combined) counts; a plain -FREE attracts in both keyword modes and restricts nothing.
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
public sealed class KeywordWishCountServiceRestrictionTests
{
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 11, 3);

    private IScheduleCommandRepository _commands = null!;
    private DataBaseContext _context = null!;
    private KeywordWishCountService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _commands = Substitute.For<IScheduleCommandRepository>();
        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);
        var options = new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new KeywordWishCountService(_context, _commands, keywordProvider, Substitute.For<IClientRepository>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase("FREE", true)]
    [TestCase("EARLY", true)]
    [TestCase("-NIGHT", true)]
    [TestCase("-FREE", false)]
    [TestCase("not-a-keyword", false)]
    public async Task HasRestrictingWishes_DependsOnWhetherTheCommandRestricts(string keyword, bool expected)
    {
        StubCommands(keyword);

        var result = await _sut.HasRestrictingWishesAsync([Agent], Day, Day, analyseToken: null, CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Test]
    public async Task HasRestrictingWishes_WithoutEmployees_IsFalse_AndReadsNothing()
    {
        var result = await _sut.HasRestrictingWishesAsync([], Day, Day, analyseToken: null, CancellationToken.None);

        result.ShouldBeFalse();
        await _commands.DidNotReceiveWithAnyArgs().GetByClientsAndDateRangeAsync(default!, default, default, default, default);
    }

    private void StubCommands(string keyword)
        => _commands.GetByClientsAndDateRangeAsync(
                Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == Agent), Day, Day, null, Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleCommand> { new() { ClientId = Agent, CurrentDate = Day, CommandKeyword = keyword } });
}
