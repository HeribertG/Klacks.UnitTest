// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SurchargeRecalculationScopeService: client resolution over client-contract assignments,
/// contract windows by scheduling rule, latest-work-date derivation (null on empty), the
/// works-in-window existence check and the unlocked real-work window (earliest to latest work with
/// LockLevel None), all restricted to real-mode, non-deleted rows.
/// </summary>

using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class SurchargeRecalculationScopeServiceTests
{
    private DataBaseContext _context = null!;
    private SurchargeRecalculationScopeService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new SurchargeRecalculationScopeService(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetClientIdsForContract_ReturnsDistinctClientsOfNonDeletedAssignments()
    {
        var contractId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var deletedClientId = Guid.NewGuid();
        _context.ClientContract.Add(BuildClientContract(contractId, clientId));
        _context.ClientContract.Add(BuildClientContract(contractId, clientId));
        var deleted = BuildClientContract(contractId, deletedClientId);
        deleted.IsDeleted = true;
        _context.ClientContract.Add(deleted);
        await _context.SaveChangesAsync();

        var result = await _sut.GetClientIdsForContractAsync(contractId);

        result.ShouldBe(new[] { clientId });
    }

    [Test]
    public async Task GetContractWindowsForRules_ReturnsOnlyContractsReferencingTheRules()
    {
        var ruleId = Guid.NewGuid();
        var referencing = BuildContract(ruleId, new DateTime(2026, 2, 1), new DateTime(2026, 12, 31));
        var other = BuildContract(Guid.NewGuid(), new DateTime(2026, 1, 1), null);
        var unbound = BuildContract(null, new DateTime(2026, 1, 1), null);
        _context.Contract.AddRange(referencing, other, unbound);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractWindowsForRulesAsync(new List<Guid> { ruleId });

        result.Count.ShouldBe(1);
        result[0].ContractId.ShouldBe(referencing.Id);
        result[0].ValidFrom.ShouldBe(new DateOnly(2026, 2, 1));
        result[0].ValidUntil.ShouldBe(new DateOnly(2026, 12, 31));
    }

    [Test]
    public async Task GetLatestWorkDate_NoWorks_ReturnsNull()
    {
        var result = await _sut.GetLatestWorkDateAsync(new List<Guid> { Guid.NewGuid() }, new DateOnly(2026, 1, 1));

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetLatestWorkDate_IgnoresScenarioRowsAndDatesBeforeFrom()
    {
        var clientId = Guid.NewGuid();
        _context.Work.Add(BuildWork(clientId, new DateOnly(2026, 3, 10)));
        _context.Work.Add(BuildWork(clientId, new DateOnly(2025, 12, 31)));
        var scenarioWork = BuildWork(clientId, new DateOnly(2026, 6, 1));
        scenarioWork.AnalyseToken = Guid.NewGuid();
        _context.Work.Add(scenarioWork);
        await _context.SaveChangesAsync();

        var result = await _sut.GetLatestWorkDateAsync(new List<Guid> { clientId }, new DateOnly(2026, 1, 1));

        result.ShouldBe(new DateOnly(2026, 3, 10));
    }

    [Test]
    public async Task HasWorksInWindow_RespectsWindowBounds()
    {
        var clientId = Guid.NewGuid();
        _context.Work.Add(BuildWork(clientId, new DateOnly(2026, 3, 10)));
        await _context.SaveChangesAsync();

        (await _sut.HasWorksInWindowAsync(new List<Guid> { clientId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31))).ShouldBeTrue();
        (await _sut.HasWorksInWindowAsync(new List<Guid> { clientId }, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30))).ShouldBeFalse();
    }

    [Test]
    public async Task GetUnlockedRealWorkWindow_NoUnlockedWorks_ReturnsNull()
    {
        var lockedWork = BuildWork(Guid.NewGuid(), new DateOnly(2026, 3, 10));
        lockedWork.LockLevel = WorkLockLevel.Closed;
        _context.Work.Add(lockedWork);
        await _context.SaveChangesAsync();

        var result = await _sut.GetUnlockedRealWorkWindowAsync();

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetUnlockedRealWorkWindow_SpansEarliestToLatestUnlockedRealWork()
    {
        _context.Work.Add(BuildWork(Guid.NewGuid(), new DateOnly(2026, 2, 5)));
        _context.Work.Add(BuildWork(Guid.NewGuid(), new DateOnly(2026, 9, 20)));

        var lockedEarlier = BuildWork(Guid.NewGuid(), new DateOnly(2026, 1, 1));
        lockedEarlier.LockLevel = WorkLockLevel.Approved;
        _context.Work.Add(lockedEarlier);

        var scenarioLater = BuildWork(Guid.NewGuid(), new DateOnly(2026, 12, 24));
        scenarioLater.AnalyseToken = Guid.NewGuid();
        _context.Work.Add(scenarioLater);

        var deletedLater = BuildWork(Guid.NewGuid(), new DateOnly(2026, 11, 11));
        deletedLater.IsDeleted = true;
        _context.Work.Add(deletedLater);

        await _context.SaveChangesAsync();

        var result = await _sut.GetUnlockedRealWorkWindowAsync();

        result.ShouldNotBeNull();
        result!.From.ShouldBe(new DateOnly(2026, 2, 5));
        result.Until.ShouldBe(new DateOnly(2026, 9, 20));
    }

    private static ClientContract BuildClientContract(Guid contractId, Guid clientId)
    {
        return new ClientContract
        {
            Id = Guid.NewGuid(),
            ContractId = contractId,
            ClientId = clientId,
            FromDate = new DateOnly(2026, 1, 1),
            IsActive = true,
        };
    }

    private static Contract BuildContract(Guid? ruleId, DateTime validFrom, DateTime? validUntil)
    {
        return new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract",
            SchedulingRuleId = ruleId,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
        };
    }

    private static Work BuildWork(Guid clientId, DateOnly date)
    {
        return new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            WorkTime = 8m,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        };
    }
}
