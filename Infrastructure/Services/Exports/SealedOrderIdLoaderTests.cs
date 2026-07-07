// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SealedOrderIdLoader against an in-memory EF Core database, verifying the
/// upward OriginalId chain walk from closed works to their sealed orders, the date-range
/// bounds, and that scenario rows (AnalyseToken) never surface a sealed order.
/// </summary>
using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class SealedOrderIdLoaderTests
{
    private DataBaseContext _context = null!;
    private SealedOrderIdLoader _loader = null!;

    private readonly Guid _sealedOrderId = Guid.NewGuid();
    private readonly Guid _originalShiftId = Guid.NewGuid();
    private readonly Guid _splitShiftId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();

    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly UntilDate = new(2026, 1, 31);
    private static readonly DateOnly InRangeDate = new(2026, 1, 10);
    private static readonly DateOnly OutOfRangeDate = new(2026, 2, 10);

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _loader = new SealedOrderIdLoader(_context);

        _context.Shift.Add(new Shift
        {
            Id = _sealedOrderId,
            Status = ShiftStatus.SealedOrder,
            Name = "Sealed order",
            Abbreviation = "SO",
            FromDate = FromDate,
        });
        _context.Shift.Add(new Shift
        {
            Id = _originalShiftId,
            Status = ShiftStatus.OriginalShift,
            Name = "Original shift",
            Abbreviation = "OS",
            OriginalId = _sealedOrderId,
            FromDate = FromDate,
        });
        _context.Shift.Add(new Shift
        {
            Id = _splitShiftId,
            Status = ShiftStatus.SplitShift,
            Name = "Split shift",
            Abbreviation = "SP",
            OriginalId = _originalShiftId,
            FromDate = FromDate,
        });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    private void AddWork(Guid shiftId, DateOnly date, WorkLockLevel lockLevel = WorkLockLevel.Closed, Guid? analyseToken = null)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            ClientId = _clientId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = lockLevel,
            AnalyseToken = analyseToken,
        });
        _context.SaveChanges();
    }

    [Test]
    public async Task LoadIdsForRangeAsync_AscendsOriginalIdChain_ToSealedOrder()
    {
        AddWork(_splitShiftId, InRangeDate);

        var result = await _loader.LoadIdsForRangeAsync(FromDate, UntilDate);

        result.ShouldBe([_sealedOrderId]);
    }

    [Test]
    public async Task LoadIdsForRangeAsync_IncludesWorkDirectlyOnSealedOrder()
    {
        AddWork(_sealedOrderId, InRangeDate);

        var result = await _loader.LoadIdsForRangeAsync(FromDate, UntilDate);

        result.ShouldBe([_sealedOrderId]);
    }

    [Test]
    public async Task LoadIdsForRangeAsync_ExcludesWorkOutsideRange()
    {
        AddWork(_splitShiftId, OutOfRangeDate);

        var result = await _loader.LoadIdsForRangeAsync(FromDate, UntilDate);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadIdsForRangeAsync_ExcludesNonClosedWork()
    {
        AddWork(_splitShiftId, InRangeDate, WorkLockLevel.Approved);

        var result = await _loader.LoadIdsForRangeAsync(FromDate, UntilDate);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadIdsForRangeAsync_ExcludesScenarioWork()
    {
        AddWork(_splitShiftId, InRangeDate, analyseToken: Guid.NewGuid());

        var result = await _loader.LoadIdsForRangeAsync(FromDate, UntilDate);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadIdsForRangeAsync_ExcludesChainThroughScenarioShift()
    {
        var scenarioShiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = scenarioShiftId,
            Status = ShiftStatus.OriginalShift,
            Name = "Scenario shift",
            Abbreviation = "SC",
            OriginalId = _sealedOrderId,
            AnalyseToken = Guid.NewGuid(),
            FromDate = FromDate,
        });
        _context.SaveChanges();
        AddWork(scenarioShiftId, InRangeDate);

        var result = await _loader.LoadIdsForRangeAsync(FromDate, UntilDate);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadIdsForRangeAsync_ReturnsEmpty_WhenChainEndsWithoutSealedOrder()
    {
        var orphanShiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = orphanShiftId,
            Status = ShiftStatus.OriginalShift,
            Name = "Orphan shift",
            Abbreviation = "OR",
            FromDate = FromDate,
        });
        _context.SaveChanges();
        AddWork(orphanShiftId, InRangeDate);

        var result = await _loader.LoadIdsForRangeAsync(FromDate, UntilDate);

        result.ShouldBeEmpty();
    }
}
