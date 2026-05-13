// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WorkMacroService — verifies macro routing for WorkChange entries.
/// Effective time computation is tested in WorkChangeEffectiveTimeServiceTests.
/// </summary>
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Schedules;
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
        _logger = Substitute.For<ILogger<WorkMacroService>>();

        _sut = new WorkMacroService(
            _context,
            _shiftRepository,
            _macroDataProvider,
            _macroCompilationService,
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

    private async Task<Work> AddWorkAsync(Guid? shiftMacroId, decimal surcharges = 0m)
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
        await _context.SaveChangesAsync();

        var shift = new Shift { Id = shiftId, MacroId = shiftMacroId };
        _shiftRepository.Get(shiftId).Returns(shift);
        return work;
    }
}
