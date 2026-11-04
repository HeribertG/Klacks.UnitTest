// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner rule (2026-11-03): if a service or a part of it (travel, briefing, debriefing) reaches outside the container, the
/// container cannot be saved, whether the save leaves the bounds untouched (sent as null, as the container dialog does),
/// sends them unchanged or moves them. Container 08:00-16:00.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ContainerWorkChildrenManagerFitTests
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
            Substitute.For<IMacroCompilationService>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<ContainerWorkChildrenManager>>());

        _parentId = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = _parentId, ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = T(8), EndTime = T(16),
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context?.Dispose();

    private Work SubWork(TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
        StartTime = start, EndTime = end,
    };

    private static WorkChange TravelBefore(Guid workId, TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.Empty, WorkId = workId, Type = WorkChangeType.CorrectionStart, StartTime = start, EndTime = end,
        ChangeTime = 1m, Description = "travel_time_before",
    };

    private async Task<Work?> Save(TimeOnly? start, TimeOnly? end, List<Work> works, List<WorkChange>? changes = null)
    {
        var parent = await _manager.UpdateChildrenAsync(
            _parentId, null, null, start, end, works, new List<Break>(), changes ?? new List<WorkChange>(), CancellationToken.None);
        await _context.SaveChangesAsync();
        return parent;
    }

    private Task<int> StoredChildren() => _context.Work.CountAsync(w => w.ParentWorkId == _parentId && !w.IsDeleted);

    private static IEnumerable<TestCaseData> Refused()
    {
        foreach (var (bounds, start, end) in new (string, TimeOnly?, TimeOnly?)[]
                 { ("BoundsNull", null, null), ("SameBoundsSent", T(8), T(16)), ("BoundsMoved", T(9), T(15)) })
        {
            yield return new TestCaseData(start, end, T(15), T(17), false).SetName($"{bounds}_ChildEndsAfterTheEnd_IsRefused");
            yield return new TestCaseData(start, end, T(7), T(9, 30), false).SetName($"{bounds}_ChildStartsBeforeTheStart_IsRefused");
            yield return new TestCaseData(start, end, T(9, 30), T(10), true).SetName($"{bounds}_TravelBeforeReachesOutside_IsRefused");
        }
    }

    [TestCaseSource(nameof(Refused))]
    public async Task ChildOrPartOfItOutsideTheContainer_IsRefused_AndNothingIsWritten(
        TimeOnly? start, TimeOnly? end, TimeOnly childStart, TimeOnly childEnd, bool withTravelFrom0745)
    {
        var work = SubWork(childStart, childEnd);
        var changes = withTravelFrom0745 ? new List<WorkChange> { TravelBefore(work.Id, T(7, 45), childStart) } : null;

        await Should.ThrowAsync<ContainerFitRefusedException>(() => Save(start, end, [work], changes));

        (await StoredChildren()).ShouldBe(0);
    }

    [Test]
    public async Task ChildWithTravelInsideTheContainer_IsSaved()
    {
        var work = SubWork(T(8, 45), T(10));
        await Save(null, null, [work], [TravelBefore(work.Id, T(8), T(8, 45))]);

        (await StoredChildren()).ShouldBe(1);
    }

    [Test]
    public async Task WorkChangeWithoutSpan_IsNotCheckedAsATimeWindow()
    {
        var work = SubWork(T(9), T(10));
        await Save(null, null, [work], [TravelBefore(work.Id, T(0), T(0))]);

        (await StoredChildren()).ShouldBe(1);
    }
}
