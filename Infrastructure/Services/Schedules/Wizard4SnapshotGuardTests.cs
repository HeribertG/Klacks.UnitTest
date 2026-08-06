// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// A background pass takes minutes; the fingerprint is what tells it afterwards whether the plan it
/// optimised still exists. It therefore has to react to every way the plan can move: an added or
/// removed entry changes the counts, an edit that keeps the count changes the newest timestamp, and a
/// scenario copy of the same day must not register at all.
/// </summary>
[TestFixture]
public sealed class Wizard4SnapshotGuardTests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly Until = new(2026, 9, 30);
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private Wizard4SnapshotGuard _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new Wizard4SnapshotGuard(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ComputeFingerprintAsync_EmptyPlan_HasNoTimestamp()
    {
        var fingerprint = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(0);
        fingerprint.BreakCount.ShouldBe(0);
        fingerprint.MaxTimestamp.ShouldBeNull();
    }

    [Test]
    public async Task ComputeFingerprintAsync_CountsWorksAndBreaksOfTheSelection()
    {
        await AddWorkAsync(AgentA, From.AddDays(1));
        await AddWorkAsync(AgentB, From.AddDays(2));
        await AddBreakAsync(AgentA, From.AddDays(3));

        var fingerprint = await _sut.ComputeFingerprintAsync([AgentA, AgentB], From, Until, CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(2);
        fingerprint.BreakCount.ShouldBe(1);
    }

    [Test]
    public async Task ComputeFingerprintAsync_IgnoresAgentsOutsideTheSelection()
    {
        await AddWorkAsync(AgentA, From.AddDays(1));
        await AddWorkAsync(AgentB, From.AddDays(1));

        var fingerprint = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(1);
    }

    [Test]
    public async Task ComputeFingerprintAsync_IgnoresDaysOutsideThePeriod()
    {
        await AddWorkAsync(AgentA, From.AddDays(-1));
        await AddWorkAsync(AgentA, Until.AddDays(1));
        await AddWorkAsync(AgentA, From);

        var fingerprint = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(1);
    }

    [Test]
    public async Task ComputeFingerprintAsync_IgnoresScenarioCopies()
    {
        // A scenario copy is not the real plan; counting it would make every open scenario look like a
        // change to the plan the optimiser started from.
        await AddWorkAsync(AgentA, From, analyseToken: Guid.NewGuid());

        var fingerprint = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(0);
    }

    [Test]
    public async Task ComputeFingerprintAsync_EditKeepingTheCount_ChangesTheTimestamp()
    {
        var work = await AddWorkAsync(AgentA, From, createTime: new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc));
        var before = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        work.UpdateTime = new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc);
        await _context.SaveChangesAsync();

        var after = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        after.WorkCount.ShouldBe(before.WorkCount);
        after.ShouldNotBe(before);
    }

    [Test]
    public async Task ComputeFingerprintAsync_AddedEntry_ChangesTheFingerprint()
    {
        await AddWorkAsync(AgentA, From);
        var before = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        await AddWorkAsync(AgentA, From.AddDays(1));
        var after = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        after.ShouldNotBe(before);
    }

    [Test]
    public async Task ComputeFingerprintAsync_UnchangedPlan_YieldsAnEqualFingerprint()
    {
        await AddWorkAsync(AgentA, From);

        var first = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);
        var second = await _sut.ComputeFingerprintAsync([AgentA], From, Until, CancellationToken.None);

        second.ShouldBe(first);
    }

    private async Task<Work> AddWorkAsync(
        Guid clientId, DateOnly date, Guid? analyseToken = null, DateTime? createTime = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            AnalyseToken = analyseToken,
            CreateTime = createTime ?? new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work;
    }

    private async Task AddBreakAsync(Guid clientId, DateOnly date)
    {
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            CreateTime = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc),
        });
        await _context.SaveChangesAsync();
    }
}
