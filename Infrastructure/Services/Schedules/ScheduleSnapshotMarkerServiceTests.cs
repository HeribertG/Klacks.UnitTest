// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// The marker decides whether an apply is rejected as stale, so it must react to everything that moves
/// a row and stay indifferent to everything that does not - a false positive blocks a legitimate apply,
/// a false negative loses the operator's manual edits.
/// </summary>
[TestFixture]
public sealed class ScheduleSnapshotMarkerServiceTests
{
    private static readonly DateOnly From = new(2026, 4, 1);
    private static readonly DateOnly Until = new(2026, 4, 30);
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private ScheduleSnapshotMarkerService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new DataBaseContext(
            new DbContextOptionsBuilder<DataBaseContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            null!);
        _sut = new ScheduleSnapshotMarkerService(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private Work AddWork(
        Guid clientId,
        DateOnly date,
        WorkLockLevel lockLevel = WorkLockLevel.None,
        TimeOnly? start = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            ShiftId = Guid.NewGuid(),
            StartTime = start ?? new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = lockLevel,
            AnalyseToken = null,
        };
        _context.Work.Add(work);
        _context.SaveChanges();
        return work;
    }

    private Task<Klacks.Api.Application.DTOs.Schedules.ScheduleSnapshotMarker> ComputeAsync()
        => _sut.ComputeAsync(From, Until, [AgentA, AgentB], analyseToken: null);

    [Test]
    public async Task SameState_ProducesTheSameMarker()
    {
        AddWork(AgentA, new DateOnly(2026, 4, 10));
        AddWork(AgentB, new DateOnly(2026, 4, 11));

        var first = await ComputeAsync();
        var second = await ComputeAsync();

        second.PlacementHash.ShouldBe(first.PlacementHash);
        second.MovableWorkCount.ShouldBe(2);
    }

    [Test]
    public async Task AddedWork_ChangesTheMarker()
    {
        AddWork(AgentA, new DateOnly(2026, 4, 10));
        var before = await ComputeAsync();

        AddWork(AgentB, new DateOnly(2026, 4, 12));
        var after = await ComputeAsync();

        after.MovableWorkCount.ShouldBe(before.MovableWorkCount + 1);
        after.PlacementHash.ShouldNotBe(before.PlacementHash);
    }

    [Test]
    public async Task RemovedWork_ChangesTheMarker()
    {
        var work = AddWork(AgentA, new DateOnly(2026, 4, 10));
        AddWork(AgentB, new DateOnly(2026, 4, 11));
        var before = await ComputeAsync();

        work.IsDeleted = true;
        await _context.SaveChangesAsync();
        var after = await ComputeAsync();

        after.MovableWorkCount.ShouldBe(before.MovableWorkCount - 1);
        after.PlacementHash.ShouldNotBe(before.PlacementHash);
    }

    [Test]
    public async Task MovedWork_ChangesTheHashWhileTheCountStays()
    {
        var work = AddWork(AgentA, new DateOnly(2026, 4, 10));
        var before = await ComputeAsync();

        work.CurrentDate = new DateOnly(2026, 4, 15);
        await _context.SaveChangesAsync();
        var after = await ComputeAsync();

        // This is exactly the case the counters cannot see.
        after.MovableWorkCount.ShouldBe(before.MovableWorkCount);
        after.PlacementHash.ShouldNotBe(before.PlacementHash);
    }

    [Test]
    public async Task ReassignedWork_ChangesTheHash()
    {
        var work = AddWork(AgentA, new DateOnly(2026, 4, 10));
        var before = await ComputeAsync();

        work.ClientId = AgentB;
        await _context.SaveChangesAsync();
        var after = await ComputeAsync();

        after.PlacementHash.ShouldNotBe(before.PlacementHash);
    }

    [Test]
    public async Task SurchargeRecompute_LeavesTheMarkerUntouched()
    {
        var work = AddWork(AgentA, new DateOnly(2026, 4, 10));
        var before = await ComputeAsync();

        // Surcharge and overtime recomputes touch work rows without moving anything; treating that as a
        // conflict would reject legitimate applies all day long.
        work.Surcharges = 12.5m;
        work.UpdateTime = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        var after = await ComputeAsync();

        after.PlacementHash.ShouldBe(before.PlacementHash);
    }

    [Test]
    public async Task LockedWorks_AreNotPartOfTheMarker()
    {
        AddWork(AgentA, new DateOnly(2026, 4, 10));
        var before = await ComputeAsync();

        AddWork(AgentB, new DateOnly(2026, 4, 12), lockLevel: WorkLockLevel.Confirmed);
        var after = await ComputeAsync();

        after.MovableWorkCount.ShouldBe(before.MovableWorkCount);
        after.PlacementHash.ShouldBe(before.PlacementHash);
    }

    [Test]
    public async Task WorkOutsideThePeriod_IsNotPartOfTheMarker()
    {
        AddWork(AgentA, new DateOnly(2026, 4, 10));
        var before = await ComputeAsync();

        AddWork(AgentB, new DateOnly(2026, 5, 20));
        var after = await ComputeAsync();

        after.PlacementHash.ShouldBe(before.PlacementHash);
    }

    [Test]
    public async Task WorkOfAnotherAgent_IsNotPartOfTheMarker()
    {
        AddWork(AgentA, new DateOnly(2026, 4, 10));
        var before = await ComputeAsync();

        AddWork(Guid.NewGuid(), new DateOnly(2026, 4, 12));
        var after = await ComputeAsync();

        after.PlacementHash.ShouldBe(before.PlacementHash);
    }

    [Test]
    public async Task StandaloneBreak_CountsWhileASubBreakDoesNot()
    {
        var parent = AddWork(AgentA, new DateOnly(2026, 4, 10));
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = AgentA,
            CurrentDate = new DateOnly(2026, 4, 14),
            ParentWorkId = null,
            AnalyseToken = null,
        });
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = AgentA,
            CurrentDate = new DateOnly(2026, 4, 15),
            ParentWorkId = parent.Id,
            AnalyseToken = null,
        });
        await _context.SaveChangesAsync();

        var marker = await ComputeAsync();

        marker.StandaloneBreakCount.ShouldBe(1);
    }
}
