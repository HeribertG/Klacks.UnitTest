// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ContainerWorkCascadeService against an in-memory DataBaseContext: verifies that moving a
/// container Work to another client also reassigns its WorkChange (child Work) and Break children, so a
/// cross-client schedule move never orphans them under the old client.
/// </summary>
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ContainerWorkCascadeServiceTests
{
    private DataBaseContext _context = null!;
    private ContainerWorkCascadeService _sut = null!;

    private readonly Guid _sourceClientId = Guid.NewGuid();
    private readonly Guid _targetClientId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 1);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new ContainerWorkCascadeService(_context, NullLogger<ContainerWorkCascadeService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task MoveChildrenAsync_ReassignsChildWorkChange_ToNewClient()
    {
        var parent = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        var workChange = new Work { Id = Guid.NewGuid(), ParentWorkId = parent.Id, ClientId = _sourceClientId, CurrentDate = _date };
        _context.Work.AddRange(parent, workChange);
        await _context.SaveChangesAsync();

        await _sut.MoveChildrenAsync(parent.Id, _date, _targetClientId);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Work.SingleAsync(w => w.Id == workChange.Id);
        reloaded.ClientId.ShouldBe(_targetClientId);
    }

    [Test]
    public async Task MoveChildrenAsync_ReassignsChildBreak_ToNewClient()
    {
        var parent = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        var childBreak = new Break { Id = Guid.NewGuid(), ParentWorkId = parent.Id, ClientId = _sourceClientId, CurrentDate = _date };
        _context.Work.Add(parent);
        _context.Break.Add(childBreak);
        await _context.SaveChangesAsync();

        await _sut.MoveChildrenAsync(parent.Id, _date, _targetClientId);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Break.SingleAsync(b => b.Id == childBreak.Id);
        reloaded.ClientId.ShouldBe(_targetClientId);
    }

    [Test]
    public async Task MoveChildrenAsync_LeavesUnrelatedWork_Untouched()
    {
        var parent = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        var unrelated = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        _context.Work.AddRange(parent, unrelated);
        await _context.SaveChangesAsync();

        await _sut.MoveChildrenAsync(parent.Id, _date, _targetClientId);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Work.SingleAsync(w => w.Id == unrelated.Id);
        reloaded.ClientId.ShouldBe(_sourceClientId);
    }
}
