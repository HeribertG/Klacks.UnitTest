// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Container rule "no overhang" on the children save path, against a real in-memory DataBaseContext:
/// whenever a save moves the container bounds (as both halves of a container split do), every sub-work
/// and sub-break must lie inside the new bounds; a save that would cut through a task is rejected before
/// anything is written. Saves that leave the bounds untouched are not checked.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ContainerWorkChildrenManagerEnvelopeTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);

    private DataBaseContext _context = null!;
    private ContainerWorkChildrenManager _manager = null!;
    private Guid _parentId;

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _manager = new ContainerWorkChildrenManager(
            _context,
            new EntityCollectionUpdateService(_context),
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IMacroDataProvider>(),
            Substitute.For<IMacroCompilationService>());

        _parentId = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = _parentId,
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            StartTime = T(7),
            EndTime = T(15),
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context?.Dispose();

    private Work SubWork(TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        ShiftId = Guid.NewGuid(),
        CurrentDate = Day,
        StartTime = start,
        EndTime = end,
    };

    private Task<Work?> Save(TimeOnly? parentStart, TimeOnly? parentEnd, List<Work> works, List<Break>? breaks = null)
        => _manager.UpdateChildrenAsync(
            _parentId, null, null, parentStart, parentEnd, works, breaks ?? new List<Break>(), new List<WorkChange>(), CancellationToken.None);

    [Test]
    public async Task ShorteningTheContainerBetweenTwoTasks_IsAccepted()
    {
        var works = new List<Work> { SubWork(T(7, 15), T(8, 30)), SubWork(T(10, 30), T(11, 30)) };

        var parent = await Save(null, T(12), works);

        parent.ShouldNotBeNull();
        parent.EndTime.ShouldBe(T(12));
    }

    [Test]
    public async Task ShorteningTheContainerThroughATask_IsRejectedWithoutTouchingTheContainer()
    {
        var works = new List<Work> { SubWork(T(7, 15), T(8, 30)), SubWork(T(10, 30), T(11, 30)) };

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => Save(null, T(11), works));

        ex.Message.ShouldContain("10:30");
        var stored = await _context.Work.SingleAsync(w => w.Id == _parentId);
        stored.EndTime.ShouldBe(T(15));
        _context.ChangeTracker.Entries<Work>().Count(e => e.State == EntityState.Added).ShouldBe(0);
    }

    [Test]
    public async Task MovingTheContainerStartThroughAnAbsence_IsRejected()
    {
        var works = new List<Work> { SubWork(T(13), T(14, 30)) };
        var breaks = new List<Break>
        {
            new() { Id = Guid.NewGuid(), AbsenceId = Guid.NewGuid(), CurrentDate = Day, StartTime = T(11, 30), EndTime = T(12, 30) },
        };

        await Should.ThrowAsync<InvalidRequestException>(() => Save(T(12), null, works, breaks));
    }

    [Test]
    public async Task SaveWithoutBoundChange_IsNotChecked()
    {
        var works = new List<Work> { SubWork(T(14), T(16)) };

        var parent = await Save(null, null, works);

        parent.ShouldNotBeNull();
        parent.EndTime.ShouldBe(T(15));
    }
}
