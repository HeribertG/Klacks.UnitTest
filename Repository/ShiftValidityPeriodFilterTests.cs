// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

/// <summary>
/// The validity period (ValidFrom/ValidUntil) of the shift list filter narrows FilterShifts to shifts whose validity
/// overlaps the period, in addition to the active/former/future flags; empty bounds leave the list alone and the
/// unsealed-draft view is never date-scoped.
/// </summary>
[TestFixture]
public class ShiftValidityPeriodFilterTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private DataBaseContext _context = null!;
    private IShiftQueryPipelineService _pipeline = null!;
    private ShiftRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _pipeline = Substitute.For<IShiftQueryPipelineService>();
        _pipeline
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _pipeline
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _pipeline
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _pipeline
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        _repository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            _pipeline,
            Substitute.For<IShiftGroupManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            new FixedCompanyClock(DateTimeOffset.UtcNow));
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void Period_KeepsOnlyOverlappingShifts_AndOpenEndedOnes()
    {
        var january = AddShift("january", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var march = AddShift("march", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        var openEnded = AddShift("open", new DateOnly(2026, 2, 1), null);

        var ids = Run(Shifts(from: new DateOnly(2026, 3, 10), until: new DateOnly(2026, 3, 20)));

        ids.ShouldBe([march.Id, openEnded.Id], ignoreOrder: true);
        ids.ShouldNotContain(january.Id);
    }

    [Test]
    public void EmptyBounds_AreNoPeriodFilter()
    {
        AddShift("january", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        AddShift("open", new DateOnly(2026, 2, 1), null);

        Run(Shifts(from: null, until: null)).Count.ShouldBe(2);
    }

    [Test]
    public void OnlyFrom_ReachesForwardWithoutLimit_OnlyUntil_BackwardWithoutLimit()
    {
        var old = AddShift("old", new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31));
        var later = AddShift("later", new DateOnly(2027, 1, 1), null);

        Run(Shifts(from: new DateOnly(2026, 6, 1), until: null)).ShouldBe([later.Id]);
        Run(Shifts(from: null, until: new DateOnly(2026, 6, 1))).ShouldBe([old.Id]);
    }

    [Test]
    public void UnsealedDraftView_IgnoresThePeriod()
    {
        var draft = AddShift("draft", new DateOnly(2027, 1, 1), null);
        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Original,
            IsSealedOrder = false,
            ValidFrom = new DateOnly(2026, 1, 1),
            ValidUntil = new DateOnly(2026, 1, 31)
        };

        Run(filter).ShouldBe([draft.Id]);
    }

    private static ShiftFilter Shifts(DateOnly? from, DateOnly? until) => new()
    {
        FilterType = ShiftFilterType.Shift,
        IsTimeRange = true,
        IsSporadic = true,
        ValidFrom = from,
        ValidUntil = until
    };

    private List<Guid> Run(ShiftFilter filter)
        => _repository.FilterShifts(filter, Today, null).Select(s => s.Id).ToList();

    private Shift AddShift(string name, DateOnly fromDate, DateOnly? untilDate)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = fromDate,
            UntilDate = untilDate,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0)
        };
        _context.Shift.Add(shift);
        _context.SaveChanges();
        return shift;
    }
}
