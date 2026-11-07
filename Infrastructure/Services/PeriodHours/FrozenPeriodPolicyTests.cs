// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// FrozenPeriodPolicy freezes a person's real-plan pay period only when it is sealed (a Closed work or break),
/// exported and older than the person's latest two distinct export ranges. A reopened period, a scenario and a
/// person with at most two export ranges have nothing frozen. The row filter judges every cached row by its own period.
/// </summary>

using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using static Klacks.UnitTest.Infrastructure.Services.PeriodHours.FrozenPeriodTestData;

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

[TestFixture]
public class FrozenPeriodPolicyTests
{
    private DataBaseContext _context = null!;
    private FrozenPeriodPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _policy = new FrozenPeriodPolicy(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task SealedExportedPeriodOlderThanTheLatestTwoExports_IsFrozen()
    {
        var clientId = Guid.NewGuid();
        FreezeJanuary(_context, clientId);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync([clientId], January.StartDate, January.EndDate, null);

        frozen.ShouldBe([clientId]);
    }

    [Test]
    public async Task APeriodInsideTheWindow_IsNotFrozen()
    {
        var clientId = Guid.NewGuid();
        FreezeJanuary(_context, clientId);
        AddWork(_context, clientId, new DateOnly(2099, 2, 10), WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync([clientId], February.StartDate, February.EndDate, null);

        frozen.ShouldBeEmpty();
    }

    [Test]
    public async Task AtMostTwoDistinctExportRanges_IncludingSupplements_FreezeNothing()
    {
        var clientId = Guid.NewGuid();
        AddWork(_context, clientId, new DateOnly(2099, 1, 10), WorkLockLevel.Closed);
        AddExports(_context, clientId, January, January, February, February);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync([clientId], January.StartDate, January.EndDate, null);

        frozen.ShouldBeEmpty();
    }

    [Test]
    public async Task AReopenedPeriod_WithoutClosedEntries_IsNotFrozen()
    {
        var clientId = Guid.NewGuid();
        AddWork(_context, clientId, new DateOnly(2099, 1, 10), WorkLockLevel.Approved);
        AddExports(_context, clientId, January, February, March);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync([clientId], January.StartDate, January.EndDate, null);

        frozen.ShouldBeEmpty();
    }

    [Test]
    public async Task AClosedBreak_SealsThePeriodLikeAClosedWork()
    {
        var clientId = Guid.NewGuid();
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2099, 1, 12),
            LockLevel = WorkLockLevel.Closed
        });
        AddExports(_context, clientId, January, February, March);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync([clientId], January.StartDate, January.EndDate, null);

        frozen.ShouldBe([clientId]);
    }

    [Test]
    public async Task AClosedScenarioWork_DoesNotSealTheRealPlan()
    {
        var clientId = Guid.NewGuid();
        AddWork(_context, clientId, new DateOnly(2099, 1, 10), WorkLockLevel.Closed, analyseToken: Guid.NewGuid());
        AddExports(_context, clientId, January, February, March);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync([clientId], January.StartDate, January.EndDate, null);

        frozen.ShouldBeEmpty();
    }

    [Test]
    public async Task AScenario_IsNeverFrozen()
    {
        var clientId = Guid.NewGuid();
        FreezeJanuary(_context, clientId);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync([clientId], January.StartDate, January.EndDate, Guid.NewGuid());

        frozen.ShouldBeEmpty();
    }

    [Test]
    public async Task OnlyTheFrozenClientsOfTheRequestedSet_AreReturned()
    {
        var frozenClient = Guid.NewGuid();
        var openClient = Guid.NewGuid();
        FreezeJanuary(_context, frozenClient);
        AddWork(_context, openClient, new DateOnly(2099, 1, 10), WorkLockLevel.Closed);
        AddExports(_context, openClient, January);
        await _context.SaveChangesAsync();

        var frozen = await _policy.GetFrozenClientIdsAsync(
            [frozenClient, openClient], January.StartDate, January.EndDate, null);

        frozen.ShouldBe([frozenClient]);
    }

    [Test]
    public async Task ExcludeFrozenRows_JudgesEveryRowByItsOwnPeriodAndPlan()
    {
        var clientId = Guid.NewGuid();
        FreezeJanuary(_context, clientId);
        await _context.SaveChangesAsync();
        var frozenRow = Row(clientId, January, null);
        var openRow = Row(clientId, February, null);
        var scenarioRow = Row(clientId, January, Guid.NewGuid());

        var kept = await _policy.ExcludeFrozenRowsAsync([frozenRow, openRow, scenarioRow]);

        kept.ShouldBe([openRow, scenarioRow], ignoreOrder: true);
    }

    private static ClientPeriodHours Row(Guid clientId, (DateOnly StartDate, DateOnly EndDate) period, Guid? analyseToken) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            StartDate = period.StartDate,
            EndDate = period.EndDate,
            AnalyseToken = analyseToken
        };
}
