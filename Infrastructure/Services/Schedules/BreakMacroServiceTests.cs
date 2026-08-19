// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for BreakMacroService.ReprocessAllBreaksAsync: sealed breaks (LockLevel != None) must never
/// be pushed through the macro pipeline again, their persisted work time stays unchanged.
/// </summary>

using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class BreakMacroServiceTests
{
    private const decimal OriginalWorkTime = 4m;
    private const decimal RecalculatedWorkTime = 8m;

    private DataBaseContext _context = null!;
    private IMacroDataProvider _macroDataProvider = null!;
    private IMacroCompilationService _macroCompilationService = null!;
    private BreakMacroService _sut = null!;

    private readonly DateOnly _startDate = new(2026, 6, 1);
    private readonly DateOnly _endDate = new(2026, 6, 30);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _macroDataProvider = Substitute.For<IMacroDataProvider>();
        _macroDataProvider.GetMacroDataForBreakAsync(Arg.Any<Break>(), Arg.Any<int?>())
            .Returns(new MacroData());

        _macroCompilationService = Substitute.For<IMacroCompilationService>();
        _macroCompilationService.CompileAndExecuteAsync(Arg.Any<Guid>(), Arg.Any<MacroData>())
            .Returns(new MacroExecutionResult(true, RecalculatedWorkTime));

        _sut = new BreakMacroService(
            _context,
            _macroDataProvider,
            _macroCompilationService,
            NullLogger<BreakMacroService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ReprocessAllBreaks_SealedBreak_IsSkippedAndItsWorkTimeStaysUnchanged()
    {
        var absenceId = await SeedAbsenceWithMacroAsync();
        var clientId = Guid.NewGuid();
        var sealedBreak = AddBreak(clientId, absenceId, new DateOnly(2026, 6, 10), WorkLockLevel.Closed);
        var openBreak = AddBreak(clientId, absenceId, new DateOnly(2026, 6, 11), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        await _sut.ReprocessAllBreaksAsync(_startDate, _endDate);

        var persistedSealed = await _context.Break.SingleAsync(b => b.Id == sealedBreak.Id);
        persistedSealed.WorkTime.ShouldBe(OriginalWorkTime);

        var persistedOpen = await _context.Break.SingleAsync(b => b.Id == openBreak.Id);
        persistedOpen.WorkTime.ShouldBe(RecalculatedWorkTime);

        await _macroDataProvider.DidNotReceive().GetMacroDataForBreakAsync(Arg.Is<Break>(b => b.Id == sealedBreak.Id), Arg.Any<int?>());
    }

    /// <summary>
    /// An absence recorded as a plain duration carries no times, so both bounds are equal and the
    /// hours live in WorkTime. The macro would derive a duration from those bounds and overwrite
    /// what the user entered, so it must not run at all.
    /// </summary>
    [Test]
    public async Task ProcessBreakMacro_DurationRecordedWithoutTimes_KeepsTheRecordedHours()
    {
        var absenceId = await SeedAbsenceWithMacroAsync();
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = absenceId,
            CurrentDate = new DateOnly(2026, 6, 12),
            WorkTime = OriginalWorkTime,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessBreakMacroAsync(breakEntry);

        breakEntry.WorkTime.ShouldBe(OriginalWorkTime);
        await _macroCompilationService.DidNotReceive()
            .CompileAndExecuteAsync(Arg.Any<Guid>(), Arg.Any<MacroData>());
    }

    /// <summary>
    /// Equal bounds with no hours are a genuinely empty entry, not a recorded duration, so the
    /// macro still decides.
    /// </summary>
    [Test]
    public async Task ProcessBreakMacro_EqualBoundsWithoutHours_StillRunsTheMacro()
    {
        var absenceId = await SeedAbsenceWithMacroAsync();
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = absenceId,
            CurrentDate = new DateOnly(2026, 6, 12),
            WorkTime = 0m,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessBreakMacroAsync(breakEntry);

        breakEntry.WorkTime.ShouldBe(RecalculatedWorkTime);
    }

    /// <summary>
    /// A real time span keeps going through the macro, including on reprocessing, so changed times
    /// are always recomputed.
    /// </summary>
    [Test]
    public async Task ProcessBreakMacro_RealTimeSpan_IsRecalculatedByTheMacro()
    {
        var absenceId = await SeedAbsenceWithMacroAsync();
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = absenceId,
            CurrentDate = new DateOnly(2026, 6, 12),
            WorkTime = OriginalWorkTime,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(12, 0),
        };

        await _sut.ProcessBreakMacroAsync(breakEntry);

        breakEntry.WorkTime.ShouldBe(RecalculatedWorkTime);
    }

    private async Task<Guid> SeedAbsenceWithMacroAsync()
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            MacroId = Guid.NewGuid(),
        };
        _context.Absence.Add(absence);
        await _context.SaveChangesAsync();
        return absence.Id;
    }

    private Break AddBreak(Guid clientId, Guid absenceId, DateOnly date, WorkLockLevel lockLevel)
    {
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = date,
            WorkTime = OriginalWorkTime,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(12, 0),
            LockLevel = lockLevel,
        };
        _context.Break.Add(breakEntry);
        return breakEntry;
    }
}
