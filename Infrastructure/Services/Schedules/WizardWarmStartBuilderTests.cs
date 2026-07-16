// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardWarmStartBuilderTests
{
    private DataBaseContext _context = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private WizardWarmStartBuilder _sut = null!;

    private static readonly DateOnly PrevFrom = new(2026, 3, 2);   // Monday
    private static readonly DateOnly PrevUntil = new(2026, 3, 8);  // Sunday
    private static readonly DateOnly TargetFrom = new(2026, 6, 1); // Monday
    private static readonly DateOnly TargetUntil = new(2026, 6, 7); // Sunday

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns((PrevFrom, PrevUntil));
        _sut = new WizardWarmStartBuilder(_context, _periodHoursService);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private Work MakeWork(Guid agent, DateOnly date, Guid? analyseToken)
    {
        return new Work
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            CurrentDate = date,
            ShiftId = Guid.NewGuid(),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
            AnalyseToken = analyseToken,
        };
    }

    [Test]
    public async Task BuildAsync_LoadsOnlyRealLedgerAndMapsAgentIdToString()
    {
        var agent = Guid.NewGuid();
        var wednesday = new DateOnly(2026, 3, 4);
        _context.Work.Add(MakeWork(agent, wednesday, analyseToken: null));                 // real ledger, in range
        _context.Work.Add(MakeWork(agent, wednesday, analyseToken: Guid.NewGuid()));        // scenario, excluded
        _context.Work.Add(MakeWork(agent, new DateOnly(2026, 2, 25), analyseToken: null));  // real but out of range
        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync([agent], TargetFrom, TargetUntil, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].AgentId.ShouldBe(agent.ToString());
        result[0].Date.ShouldBe(new DateOnly(2026, 6, 3)); // Wednesday -> Wednesday
        result[0].TotalHours.ShouldBe(8m);
    }

    [Test]
    public async Task BuildAsync_EmptyPreviousPeriod_ReturnsEmpty()
    {
        var agent = Guid.NewGuid();
        // A real work that lies OUTSIDE the previous period boundaries.
        _context.Work.Add(MakeWork(agent, new DateOnly(2026, 1, 15), analyseToken: null));
        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync([agent], TargetFrom, TargetUntil, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task BuildAsync_EmptyAgentList_ReturnsEmptyWithoutQueryingBoundaries()
    {
        var result = await _sut.BuildAsync([], TargetFrom, TargetUntil, CancellationToken.None);

        result.ShouldBeEmpty();
        await _periodHoursService.DidNotReceive().GetPeriodBoundariesAsync(Arg.Any<DateOnly>());
    }
}
