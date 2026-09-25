// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroDryRunService: it counts the live, non-scenario entries of every holder in scope (the shifts of a
/// cut group or one absence type) and the sealed ones, samples only the most recent open entries across all of them
/// (capped at the sample size, newest first), evaluates each entry with the current and the new macro of its own holder on
/// the production inputs, lets a holder that keeps its macro count without changing, keeps a directly recorded break
/// duration without running a macro, reports a new macro that no longer exists or does not compile instead of sampling,
/// leaves the current value empty for a current macro that no longer exists or does not compile (the sample then compares
/// the stored value, as production keeps it), supports removing the macro, never saves and leaves nothing tracked on either target, stops with
/// OperationCanceledException on the caller's cancellation and flags an exhausted time budget.
/// </summary>

using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroDryRunServiceTests
{
    private const string HourScript = "IMPORT Hour\nOUTPUT 1, Hour";
    private const string DoubleHourScript = "IMPORT Hour\nOUTPUT 1, Hour * 2";
    private const string TripleHourScript = "IMPORT Hour\nOUTPUT 1, Hour * 3";
    private const string DuplicateImportScript = "IMPORT Hour\nIMPORT Hour\nOUTPUT 1, Hour";
    private const string SlowScript = "DIM I\nFOR I = 1 TO 100000\nNEXT\nOUTPUT 1, 1";
    private const decimal StoredSurcharges = 1.5m;
    private const decimal BreakHours = 8m;
    private const int EntriesBeyondTheSample = 5;

    private DataBaseContext _context = null!;
    private IMacroDataProvider _macroDataProvider = null!;
    private MacroDryRunService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _macroDataProvider = Substitute.For<IMacroDataProvider>();
        _macroDataProvider.GetMacroDataAsync(Arg.Any<Work>())
            .Returns(ci => new MacroData { Hour = ci.Arg<Work>().WorkTime });
        _macroDataProvider.GetMacroDataForBreakAsync(Arg.Any<Break>(), Arg.Any<int?>())
            .Returns(new MacroData { Hour = BreakHours });
        _sut = new MacroDryRunService(_context, _macroDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Shift_CountsLiveEntries_SamplesOnlyOpenOnes_NewestFirst()
    {
        var shiftId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        var next = await AddMacroAsync(DoubleHourScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10), workTime: 8m);
        AddWork(shiftId, new DateOnly(2026, 6, 11), workTime: 4m);
        AddWork(shiftId, new DateOnly(2026, 6, 12), WorkLockLevel.Confirmed);
        AddWork(shiftId, new DateOnly(2026, 6, 13), analyseToken: Guid.NewGuid());
        AddWork(Guid.NewGuid(), new DateOnly(2026, 6, 14));
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, current, next)]);

        result.TotalEntries.ShouldBe(3);
        result.SealedEntries.ShouldBe(1);
        result.OpenEntries.ShouldBe(2);
        result.Samples.Select(s => s.Date).ShouldBe(new[] { new DateOnly(2026, 6, 11), new DateOnly(2026, 6, 10) });
        result.Samples[0].StoredValue.ShouldBe(StoredSurcharges);
        result.Samples[0].CurrentValue.ShouldBe(4m);
        result.Samples[0].NewValue.ShouldBe(8m);
        result.ChangedSamples.ShouldBe(2);
        result.NewMacroError.ShouldBeNull();
        result.BudgetExceeded.ShouldBeFalse();
    }

    [Test]
    public async Task CutGroup_CountsEveryShift_AndEvaluatesEachEntryWithItsOwnCurrentMacro()
    {
        var firstCut = Guid.NewGuid();
        var secondCut = Guid.NewGuid();
        var hour = await AddMacroAsync(HourScript);
        var triple = await AddMacroAsync(TripleHourScript);
        var next = await AddMacroAsync(DoubleHourScript);
        AddWork(firstCut, new DateOnly(2026, 6, 10), workTime: 8m);
        AddWork(secondCut, new DateOnly(2026, 6, 11), workTime: 4m);
        AddWork(secondCut, new DateOnly(2026, 6, 12), WorkLockLevel.Approved);
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(
            MacroAssignmentTarget.Shift,
            [new MacroDryRunHolder(firstCut, hour, next), new MacroDryRunHolder(secondCut, triple, next)]);

        result.TotalEntries.ShouldBe(3);
        result.SealedEntries.ShouldBe(1);
        result.Samples.Select(s => s.Date).ShouldBe(new[] { new DateOnly(2026, 6, 11), new DateOnly(2026, 6, 10) });
        result.Samples[0].CurrentValue.ShouldBe(12m);
        result.Samples[0].NewValue.ShouldBe(8m);
        result.Samples[1].CurrentValue.ShouldBe(8m);
        result.Samples[1].NewValue.ShouldBe(16m);
    }

    [Test]
    public async Task HolderThatKeepsItsMacro_IsCounted_ButNeverChanges()
    {
        var shiftId = Guid.NewGuid();
        var next = await AddMacroAsync(DoubleHourScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10));
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, next, next)]);

        result.TotalEntries.ShouldBe(1);
        result.Samples.Single().Changes.ShouldBeFalse();
        result.ChangedSamples.ShouldBe(0);
    }

    [Test]
    public async Task Shift_SampleIsCappedAtTheSampleSize()
    {
        var shiftId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        var next = await AddMacroAsync(DoubleHourScript);
        var firstDay = new DateOnly(2026, 1, 1);
        var entryCount = MacroDryRunService.SampleSize + EntriesBeyondTheSample;
        for (var day = 0; day < entryCount; day++)
        {
            AddWork(shiftId, firstDay.AddDays(day));
        }
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, current, next)]);

        result.TotalEntries.ShouldBe(entryCount);
        result.Samples.Count.ShouldBe(MacroDryRunService.SampleSize);
        result.Samples[0].Date.ShouldBe(firstDay.AddDays(entryCount - 1));
    }

    [Test]
    public async Task NewMacroThatDoesNotCompile_IsReported_AndNothingIsSampled()
    {
        var shiftId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        var broken = await AddMacroAsync(DuplicateImportScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10));
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, current, broken)]);

        result.NewMacroError!.ShouldContain("does not compile");
        result.TotalEntries.ShouldBe(1);
        result.Samples.ShouldBeEmpty();
        await _macroDataProvider.DidNotReceive().GetMacroDataAsync(Arg.Any<Work>());
    }

    [Test]
    public async Task NewMacroThatNoLongerExists_IsReported()
    {
        var shiftId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10));
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(
            MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, current, Guid.NewGuid())]);

        result.NewMacroError!.ShouldContain("no longer exists");
    }

    [Test]
    public async Task CurrentMacroThatDoesNotCompile_LeavesNoCurrentValue_AndComparesWithTheStoredValue()
    {
        var shiftId = Guid.NewGuid();
        var broken = await AddMacroAsync(DuplicateImportScript);
        var next = await AddMacroAsync(HourScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10), workTime: StoredSurcharges);
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, broken, next)]);

        result.NewMacroError.ShouldBeNull();
        var sample = result.Samples.Single();
        sample.CurrentValue.ShouldBeNull();
        sample.NewValue.ShouldBe(StoredSurcharges);
        sample.Changes.ShouldBeFalse();
    }

    [Test]
    public async Task CurrentMacroThatNoLongerExists_LeavesNoCurrentValue_AndComparesWithTheStoredValue()
    {
        var shiftId = Guid.NewGuid();
        var next = await AddMacroAsync(HourScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10), workTime: 4m);
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(
            MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, Guid.NewGuid(), next)]);

        var sample = result.Samples.Single();
        sample.CurrentValue.ShouldBeNull();
        sample.NewValue.ShouldBe(4m);
        sample.Changes.ShouldBeTrue();
    }

    [Test]
    public async Task RemovingTheMacro_LeavesNoNewValue()
    {
        var shiftId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10));
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, current, null)]);

        var sample = result.Samples.Single();
        sample.CurrentValue.ShouldBe(8m);
        sample.NewValue.ShouldBeNull();
        sample.Changes.ShouldBeTrue();
    }

    [Test]
    public async Task AbsenceType_DirectlyRecordedDuration_KeepsItsValue_WithoutRunningAMacro()
    {
        var absenceTypeId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        var next = await AddMacroAsync(DoubleHourScript);
        var recorded = AddBreak(absenceTypeId, new DateOnly(2026, 6, 10), TimeOnly.MinValue, TimeOnly.MinValue, 4m);
        AddBreak(absenceTypeId, new DateOnly(2026, 6, 11), new TimeOnly(8, 0), new TimeOnly(12, 0), 0m);
        await SaveAndClearAsync();

        var result = await _sut.RunAsync(
            MacroAssignmentTarget.AbsenceType, [new MacroDryRunHolder(absenceTypeId, current, next)]);

        result.TotalEntries.ShouldBe(2);
        var kept = result.Samples.Single(s => s.EntryId == recorded.Id);
        kept.KeepsRecordedValue.ShouldBeTrue();
        kept.StoredValue.ShouldBe(4m);
        kept.Changes.ShouldBeFalse();
        var computed = result.Samples.Single(s => s.EntryId != recorded.Id);
        computed.CurrentValue.ShouldBe(BreakHours);
        computed.NewValue.ShouldBe(BreakHours * 2);
        await _macroDataProvider.Received(1).GetMacroDataForBreakAsync(Arg.Any<Break>(), Arg.Any<int?>());
    }

    [Test]
    public async Task Shift_NeverSaves_AndLeavesNothingTracked()
    {
        var shiftId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        var next = await AddMacroAsync(DoubleHourScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10));
        await SaveAndClearAsync();
        var saves = CountSaves();

        var result = await _sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, current, next)]);

        result.Samples.Count.ShouldBe(1);
        saves().ShouldBe(0);
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task AbsenceType_NeverSaves_AndLeavesNothingTracked()
    {
        var absenceTypeId = Guid.NewGuid();
        var current = await AddMacroAsync(HourScript);
        var next = await AddMacroAsync(DoubleHourScript);
        AddBreak(absenceTypeId, new DateOnly(2026, 6, 10), TimeOnly.MinValue, TimeOnly.MinValue, 4m);
        AddBreak(absenceTypeId, new DateOnly(2026, 6, 11), new TimeOnly(8, 0), new TimeOnly(12, 0), 0m);
        await SaveAndClearAsync();
        var saves = CountSaves();

        var result = await _sut.RunAsync(
            MacroAssignmentTarget.AbsenceType, [new MacroDryRunHolder(absenceTypeId, current, next)]);

        result.Samples.Count.ShouldBe(2);
        saves().ShouldBe(0);
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task CallerCancellation_Throws()
    {
        await Should.ThrowAsync<OperationCanceledException>(() => _sut.RunAsync(
            MacroAssignmentTarget.Shift, [new MacroDryRunHolder(Guid.NewGuid(), null, null)], new CancellationToken(true)));
    }

    [Test]
    public async Task ExhaustedBudget_ReturnsAFlaggedPartialResult()
    {
        var shiftId = Guid.NewGuid();
        var slow = await AddMacroAsync(SlowScript);
        AddWork(shiftId, new DateOnly(2026, 6, 10));
        AddWork(shiftId, new DateOnly(2026, 6, 11));
        AddWork(shiftId, new DateOnly(2026, 6, 12));
        await SaveAndClearAsync();
        var sut = new MacroDryRunService(_context, _macroDataProvider, TimeSpan.FromMilliseconds(1));

        var result = await sut.RunAsync(MacroAssignmentTarget.Shift, [new MacroDryRunHolder(shiftId, slow, slow)]);

        result.BudgetExceeded.ShouldBeTrue();
        result.Samples.Count.ShouldBeLessThan(3);
    }

    private Func<int> CountSaves()
    {
        var saves = 0;
        _context.SavingChanges += (_, _) => saves++;
        return () => saves;
    }

    private async Task<Guid> AddMacroAsync(string content)
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = Guid.NewGuid().ToString("N"),
            Content = content,
            Description = new MultiLanguage()
        };
        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();
        return macro.Id;
    }

    private Work AddWork(
        Guid shiftId,
        DateOnly date,
        WorkLockLevel lockLevel = WorkLockLevel.None,
        decimal workTime = 8m,
        Guid? analyseToken = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            ClientId = Guid.NewGuid(),
            CurrentDate = date,
            WorkTime = workTime,
            Surcharges = StoredSurcharges,
            StartTime = new TimeOnly(7, 0),
            EndTime = new TimeOnly(15, 0),
            LockLevel = lockLevel,
            AnalyseToken = analyseToken
        };
        _context.Work.Add(work);
        return work;
    }

    private Break AddBreak(Guid absenceTypeId, DateOnly date, TimeOnly start, TimeOnly end, decimal workTime)
    {
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = absenceTypeId,
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = workTime
        };
        _context.Break.Add(breakEntry);
        return breakEntry;
    }

    private async Task SaveAndClearAsync()
    {
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
