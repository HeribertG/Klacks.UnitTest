// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards which shifts the harmonizer apply asks the scenario clone to carry. A shift without a single work is
/// not in the bitmap, so the listed ids alone must never decide the scenario's shift set: a group-scoped clone
/// adds the group's shifts itself, a group-less clone of a scenario source lists all of that scenario's shifts,
/// and a group-less clone of the real plan lists nothing so that every real shift is cloned.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class HarmonizerApplyServiceShiftSetTests
{
    private static readonly DateOnly D = new(2026, 4, 20);

    private DataBaseContext _context = null!;
    private HarmonizerResultCache _cache = null!;
    private IAnalyseScenarioService _scenarioService = null!;

    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _bitmapShiftId = Guid.NewGuid();
    private IReadOnlyCollection<Guid>? _clonedShiftIds;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _context.Work.Add(new Work
        {
            Id = _workId,
            ClientId = Guid.NewGuid(),
            ShiftId = _bitmapShiftId,
            CurrentDate = D,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
        });
        _context.SaveChanges();

        _cache = new HarmonizerResultCache();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _scenarioService
            .CloneScenarioDataWithMapsAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(),
                Arg.Do<IReadOnlyCollection<Guid>?>(ids => _clonedShiftIds = ids), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<Guid, Guid> { [_bitmapShiftId] = Guid.NewGuid() }, new Dictionary<Guid, Guid>()));
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private HarmonizerApplyService BuildSut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { CreatedIds = [] });

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<(IReadOnlyList<Guid> Ids, Guid Token, AnalyseScenario Scenario)>>>())
            .Returns(ci => ci.Arg<Func<Task<(IReadOnlyList<Guid> Ids, Guid Token, AnalyseScenario Scenario)>>>()());

        var scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        scenarioRepository.GetByTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { RunGroupId = Guid.NewGuid() });

        var nameGenerator = Substitute.For<IScenarioNameGenerator>();
        nameGenerator
            .GenerateAsync(
                Arg.Any<ScenarioNameKind>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("generated name");

        var complianceService = Substitute.For<IScenarioComplianceService>();
        complianceService
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport([], []));

        return new HarmonizerApplyService(
            _cache, mediator, scenarioRepository, _scenarioService, unitOfWork, _context,
            Substitute.For<IWizardRunCaptureRepository>(), complianceService,
            Substitute.For<IScheduleTimelineService>(), Substitute.For<IScheduleSnapshotMarkerService>(),
            new FixedCompanyClock(new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero)),
            nameGenerator, NullLogger<HarmonizerApplyService>.Instance);
    }

    private HarmonyBitmap Bitmap()
    {
        var cells = new Cell[1, 1];
        cells[0, 0] = new Cell(CellSymbol.Early, _bitmapShiftId, [_workId], false,
            D.ToDateTime(new TimeOnly(8, 0)), D.ToDateTime(new TimeOnly(16, 0)), 8m);
        return new HarmonyBitmap([new BitmapAgent(Guid.NewGuid().ToString(), "A", 8m, new HashSet<CellSymbol>())], [D], cells);
    }

    private Guid StoreResult(Guid? sourceAnalyseToken)
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, Bitmap(), Bitmap(), sourceAnalyseToken: sourceAnalyseToken);
        return jobId;
    }

    private async Task<Guid> AddScenarioShiftAsync(Guid token, bool isDeleted = false)
    {
        var shiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = shiftId, Name = "S", AnalyseToken = token, IsDeleted = isDeleted });
        await _context.SaveChangesAsync();
        return shiftId;
    }

    [Test]
    public async Task GroupScopedApply_ListsTheBitmapShifts_AndLeavesTheGroupShiftsToTheClone()
    {
        await BuildSut().ApplyAsScenarioAsync(StoreResult(Guid.NewGuid()), Guid.NewGuid(), CancellationToken.None);

        _clonedShiftIds.ShouldNotBeNull();
        _clonedShiftIds!.ShouldBe(new[] { _bitmapShiftId });
    }

    [Test]
    public async Task GrouplessApplyOfAScenarioSource_ListsEveryShiftOfThatScenario()
    {
        var sourceToken = Guid.NewGuid();
        var otherToken = Guid.NewGuid();
        var first = await AddScenarioShiftAsync(sourceToken);
        var second = await AddScenarioShiftAsync(sourceToken);
        await AddScenarioShiftAsync(sourceToken, isDeleted: true);
        await AddScenarioShiftAsync(otherToken);

        await BuildSut().ApplyAsScenarioAsync(StoreResult(sourceToken), null, CancellationToken.None);

        _clonedShiftIds.ShouldNotBeNull();
        _clonedShiftIds!.ShouldBe(
            new[] { first, second, _bitmapShiftId }, ignoreOrder: true,
            "all live shifts of the source scenario plus the bitmap's own, none of a deleted or foreign scenario");
    }

    [Test]
    public async Task GrouplessApplyOfTheRealPlan_ListsNoShift_SoEveryRealShiftIsCloned()
    {
        await BuildSut().ApplyAsScenarioAsync(StoreResult(null), null, CancellationToken.None);

        _clonedShiftIds.ShouldNotBeNull();
        _clonedShiftIds!.ShouldBeEmpty();
    }
}
