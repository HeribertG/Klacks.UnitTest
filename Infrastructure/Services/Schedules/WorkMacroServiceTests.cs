// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for WorkMacroService.ProcessWorkChangeMacroAsync, covering proportional surcharge
/// calculation for duration-only WorkChange entries and macro-based calculation for time-range types.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WorkMacroServiceTests
{
    private DataBaseContext _context = null!;
    private IShiftRepository _shiftRepository = null!;
    private IMacroDataProvider _macroDataProvider = null!;
    private IMacroCompilationService _macroCompilationService = null!;
    private ILogger<WorkMacroService> _logger = null!;
    private WorkMacroService _sut = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        _shiftRepository = Substitute.For<IShiftRepository>();
        _macroDataProvider = Substitute.For<IMacroDataProvider>();
        _macroCompilationService = Substitute.For<IMacroCompilationService>();
        _logger = Substitute.For<ILogger<WorkMacroService>>();

        _sut = new WorkMacroService(
            _context,
            _shiftRepository,
            _macroDataProvider,
            _macroCompilationService,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_DurationOnly_ProportionalSurcharge()
    {
        var shiftId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            WorkTime = 8m,
            Surcharges = 0.8m,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 1m / 3m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            Surcharges = 0m,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        var expected = Math.Round((1m / 3m) / 8m * 0.8m, 2);
        workChange.Surcharges.ShouldBe(expected);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_DurationOnly_ZeroSurchargeWhenWorkHasNone()
    {
        var shiftId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            WorkTime = 8m,
            Surcharges = 0m,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 0.5m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            Surcharges = 0.2m,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.Surcharges.ShouldBe(0m);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_DurationOnly_ZeroWhenWorkTimeIsZero()
    {
        var shiftId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            WorkTime = 0m,
            Surcharges = 1m,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(6, 0),
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.TravelEnd,
            ChangeTime = 0.5m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            Surcharges = 0m,
        };

        await Should.NotThrowAsync(() => _sut.ProcessWorkChangeMacroAsync(workChange));
        workChange.Surcharges.ShouldBe(0m);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_TimeRangeType_UsesMacroNotProportional()
    {
        var macroId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            WorkTime = 8m,
            Surcharges = 0.8m,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();

        var shift = new Shift
        {
            Id = shiftId,
            MacroId = macroId,
            Name = "TestShift",
            Abbreviation = "TS",
        };
        _shiftRepository.Get(shiftId).Returns(Task.FromResult<Shift?>(shift));
        _macroDataProvider
            .GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), Arg.Any<Work>())
            .Returns(Task.FromResult(new MacroData()));
        _macroCompilationService
            .CompileAndExecuteAsync(macroId, Arg.Any<MacroData>())
            .Returns(Task.FromResult(new MacroExecutionResult(true, 0.5m)));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.TravelWithin,
            ChangeTime = 0m,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            Surcharges = 0m,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.Surcharges.ShouldBe(0.5m);
        await _macroCompilationService.Received(1).CompileAndExecuteAsync(macroId, Arg.Any<MacroData>());
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_DurationOnly_ProportionalEvenWithMacroAssigned()
    {
        var macroId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            WorkTime = 8m,
            Surcharges = 2m,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();

        var shift = new Shift
        {
            Id = shiftId,
            MacroId = macroId,
            Name = "TestShift",
            Abbreviation = "TS",
        };
        _shiftRepository.Get(shiftId).Returns(Task.FromResult<Shift?>(shift));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.Briefing,
            ChangeTime = 0.5m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            Surcharges = 0m,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        var expected = Math.Round(0.5m / 8m * 2m, 2);
        workChange.Surcharges.ShouldBe(expected);
        await _macroCompilationService.DidNotReceive().CompileAndExecuteAsync(Arg.Any<Guid>(), Arg.Any<MacroData>());
    }
}
