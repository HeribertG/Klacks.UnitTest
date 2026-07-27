// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WorkMacroService — verifies macro routing for WorkChange entries.
/// Effective time computation is tested in WorkChangeEffectiveTimeServiceTests.
/// </summary>
using System.Linq;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Macros;
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
    private IOvertimeSurchargeCalculator _overtimeSurchargeCalculator = null!;
    private ILogger<WorkMacroService> _logger = null!;
    private WorkMacroService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _shiftRepository = Substitute.For<IShiftRepository>();
        _macroDataProvider = Substitute.For<IMacroDataProvider>();
        _macroCompilationService = Substitute.For<IMacroCompilationService>();
        _overtimeSurchargeCalculator = Substitute.For<IOvertimeSurchargeCalculator>();
        _overtimeSurchargeCalculator.CalculateAsync(Arg.Any<Work>())
            .Returns(OvertimeCalculationResult.None());
        _logger = Substitute.For<ILogger<WorkMacroService>>();

        _sut = new WorkMacroService(
            _context,
            _shiftRepository,
            _macroDataProvider,
            _macroCompilationService,
            _overtimeSurchargeCalculator,
            _logger);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ProcessWorkChangeMacroAsync_WithMacro_SetsSurchargesFromMacroResult()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData();
        _macroDataProvider.GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), work)
            .Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(true, 0.42m));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd, ChangeTime = 0.25m,
            StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.Surcharges.ShouldBe(0.42m);
        await _macroCompilationService.Received(1).CompileAndExecuteAsync(macroId, macroData);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_DurationOnly_WithMacro_MacroIsCalled()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid(), surcharges: 0.8m);
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        _macroDataProvider.GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), work)
            .Returns(new MacroData());
        _macroCompilationService.CompileAndExecuteAsync(macroId, Arg.Any<MacroData>())
            .Returns(new MacroExecutionResult(true, 0.1m));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.ReplacementEnd, ChangeTime = 0.25m,
            StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        await _macroCompilationService.Received(1).CompileAndExecuteAsync(macroId, Arg.Any<MacroData>());
        workChange.Surcharges.ShouldBe(0.1m);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_NoMacroOnShift_SurchargesUnchanged()
    {
        var work = await AddWorkAsync(shiftMacroId: null);

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd, ChangeTime = 0.25m,
            Surcharges = 0.99m,
            StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.Surcharges.ShouldBe(0.99m);
        await _macroCompilationService.DidNotReceive()
            .CompileAndExecuteAsync(Arg.Any<Guid>(), Arg.Any<MacroData>());
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_TravelWithin_MacroIsCalled()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData();
        _macroDataProvider.GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), work)
            .Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(true, 0.5m));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.TravelWithin, ChangeTime = 0.25m,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(8, 15),
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.Surcharges.ShouldBe(0.5m);
        await _macroCompilationService.Received(1).CompileAndExecuteAsync(macroId, macroData);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_FixedPerShiftMode_OverridesToFlatRatePerShift()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData { NightRateMode = SurchargeRateMode.FixedPerShift, NightRate = 20m };
        _macroDataProvider.GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), work)
            .Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                8.0m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 8.0m) }));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd, ChangeTime = 0.25m,
            StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.Surcharges.ShouldBe(20.0m);
        workChange.SurchargeItems.ShouldHaveSingleItem();
        workChange.SurchargeItems.Single().Type.ShouldBe(SurchargeType.Night);
        workChange.SurchargeItems.Single().Amount.ShouldBe(20m);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_FixedPerShiftMode_ZeroMacroAmount_StaysZero()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData { NightRateMode = SurchargeRateMode.FixedPerShift, NightRate = 20m };
        _macroDataProvider.GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), work)
            .Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                0m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 0m) }));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd, ChangeTime = 0.25m,
            StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.Surcharges.ShouldBe(0m);
        workChange.SurchargeItems.Single().Amount.ShouldBe(0m);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_MultiplierWithMinimumPerHour_UsesFloorWhenHigher()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData
        {
            WE1RateMode = SurchargeRateMode.Multiplier,
            WE1Rate = 0.26m,
            WE1MinimumPerHour = 75.0m,
        };
        _macroDataProvider.GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), work)
            .Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                2.08m,
                new List<MacroSurchargeItem> { new(SurchargeType.Weekend1, 2.08m) }));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd, ChangeTime = 0.25m,
            StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.SurchargeItems.Single().Amount.ShouldBe(600m);
        workChange.Surcharges.ShouldBe(600m);
    }

    [Test]
    public async Task ProcessWorkChangeMacroAsync_FixedPerHourMode_ArithmeticUnchanged()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData { NightRateMode = SurchargeRateMode.FixedPerHour, NightRate = 0.73m };
        _macroDataProvider.GetMacroDataForWorkChangeAsync(Arg.Any<WorkChange>(), work)
            .Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                5.84m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 5.84m) }));

        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd, ChangeTime = 0.25m,
            StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
        };

        await _sut.ProcessWorkChangeMacroAsync(workChange);

        workChange.SurchargeItems.Single().Amount.ShouldBe(5.84m);
        workChange.Surcharges.ShouldBe(5.84m);
    }

    [Test]
    public async Task ProcessWorkMacroAsync_OvertimeNotConfigured_RegressionUnchangedFromMacroResult()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData();
        _macroDataProvider.GetMacroDataAsync(work).Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                2.0m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 2.0m) }));

        await _sut.ProcessWorkMacroAsync(work);

        work.Surcharges.ShouldBe(2.0m);
        work.SurchargeItems.Single().Type.ShouldBe(SurchargeType.Night);
        work.SurchargeItems.Single().Amount.ShouldBe(2.0m);
    }

    [Test]
    public async Task ProcessWorkMacroAsync_HighestWinsOvertimeLowerThanExisting_ResultUnchanged()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData();
        _macroDataProvider.GetMacroDataAsync(work).Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                5.0m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 5.0m) }));
        _overtimeSurchargeCalculator.CalculateAsync(work).Returns(new OvertimeCalculationResult(
            new List<MacroSurchargeItem> { new(SurchargeType.Overtime1, 3.0m) }));

        await _sut.ProcessWorkMacroAsync(work);

        work.Surcharges.ShouldBe(5.0m);
        work.SurchargeItems.Single().Type.ShouldBe(SurchargeType.Night);
    }

    [Test]
    public async Task ProcessWorkMacroAsync_HighestWinsOvertimeHigherThanExisting_ReplacesSurchargePortionPreservingNonSurchargeDelta()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid());
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData();
        _macroDataProvider.GetMacroDataAsync(work).Returns(macroData);
        // ResultValue (5.0) carries a non-surcharge portion beyond the typed surcharges (2.0) — e.g. a
        // custom macro mixing a surcharge with a passthrough quantity, analogous to the seeded
        // "Accident" macro's plain Hour output.
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                5.0m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 2.0m) }));
        _overtimeSurchargeCalculator.CalculateAsync(work).Returns(new OvertimeCalculationResult(
            new List<MacroSurchargeItem> { new(SurchargeType.Overtime1, 4.0m) }));

        await _sut.ProcessWorkMacroAsync(work);

        // delta = overtimeTotal(4.0) - existingSurchargeTotal(2.0) = 2.0; adjustedResultValue = 5.0 + 2.0
        work.Surcharges.ShouldBe(7.0m);
        work.SurchargeItems.Single().Type.ShouldBe(SurchargeType.Overtime1);
        work.SurchargeItems.Single().Amount.ShouldBe(4.0m);
    }

    [Test]
    public async Task ProcessWorkMacroAsync_AdditiveMacroFunction_AddsOvertimeOnTopOfExistingSurcharge()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid(), macroFunction: MacroFunctionEnum.StandardAdditive);
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData();
        _macroDataProvider.GetMacroDataAsync(work).Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                2.0m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 2.0m) }));
        _overtimeSurchargeCalculator.CalculateAsync(work).Returns(new OvertimeCalculationResult(
            new List<MacroSurchargeItem> { new(SurchargeType.Overtime1, 1.0m) }));

        await _sut.ProcessWorkMacroAsync(work);

        work.Surcharges.ShouldBe(3.0m);
        work.SurchargeItems.Count.ShouldBe(2);
        work.SurchargeItems.ShouldContain(i => i.Type == SurchargeType.Night && i.Amount == 2.0m);
        work.SurchargeItems.ShouldContain(i => i.Type == SurchargeType.Overtime1 && i.Amount == 1.0m);
    }

    [Test]
    public async Task ProcessWorkMacroAsync_AdditiveWithFixedPerShiftNightSurcharge_AddsCleanly()
    {
        var work = await AddWorkAsync(shiftMacroId: Guid.NewGuid(), macroFunction: MacroFunctionEnum.StandardAdditive);
        var macroId = (await _shiftRepository.Get(work.ShiftId))!.MacroId!.Value;

        var macroData = new MacroData { NightRateMode = SurchargeRateMode.FixedPerShift, NightRate = 20m };
        _macroDataProvider.GetMacroDataAsync(work).Returns(macroData);
        _macroCompilationService.CompileAndExecuteAsync(macroId, macroData)
            .Returns(new MacroExecutionResult(
                true,
                8.0m,
                new List<MacroSurchargeItem> { new(SurchargeType.Night, 8.0m) }));
        _overtimeSurchargeCalculator.CalculateAsync(work).Returns(new OvertimeCalculationResult(
            new List<MacroSurchargeItem> { new(SurchargeType.Overtime1, 5.0m) }));

        await _sut.ProcessWorkMacroAsync(work);

        // ApplyRateModeAdjustments first turns the FixedPerShift Night amount into the flat rate (20.0),
        // then Additive stacking adds the overtime portion (5.0) on top — 25.0 total, two items.
        work.Surcharges.ShouldBe(25.0m);
        work.SurchargeItems.Count.ShouldBe(2);
        work.SurchargeItems.ShouldContain(i => i.Type == SurchargeType.Night && i.Amount == 20.0m);
        work.SurchargeItems.ShouldContain(i => i.Type == SurchargeType.Overtime1 && i.Amount == 5.0m);
    }

    private async Task<Work> AddWorkAsync(
        Guid? shiftMacroId,
        decimal surcharges = 0m,
        MacroFunctionEnum macroFunction = MacroFunctionEnum.Standard)
    {
        var shiftId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(), ShiftId = shiftId, ClientId = Guid.NewGuid(),
            WorkTime = 8m, Surcharges = surcharges,
            StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(15, 0),
            CurrentDate = DateOnly.FromDateTime(DateTime.Today),
        };
        _context.Work.Add(work);

        if (shiftMacroId.HasValue)
        {
            _context.Macro.Add(new Macro
            {
                Id = shiftMacroId.Value,
                Name = "TestMacro",
                Content = string.Empty,
                Description = new MultiLanguage(),
                Category = MacroCategoryEnum.Shift,
                Type = (int)macroFunction,
            });
        }

        await _context.SaveChangesAsync();

        var shift = new Shift { Id = shiftId, MacroId = shiftMacroId };
        _shiftRepository.Get(shiftId).Returns(shift);
        return work;
    }
}
