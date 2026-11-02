// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Recalculation after an owner change against a real in-memory DataBaseContext: the stored children are processed in the
/// order of a save (sub-works, the work, then the WorkChanges of both), a deleted child stays out, and an unpaid sub-break
/// is judged for the new owner and negates its surcharge on the work.
/// </summary>

using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ContainerWorkChildrenManagerNewOwnerTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly Guid NewOwner = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private IWorkMacroService _workMacros = null!;
    private IMacroDataProvider _macroData = null!;
    private IMacroCompilationService _macroCompilation = null!;
    private ILogger<ContainerWorkChildrenManager> _logger = null!;
    private ContainerWorkChildrenManager _manager = null!;
    private Work _parent = null!;
    private Work _subWork = null!;
    private WorkChange _parentChange = null!;
    private WorkChange _subChange = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _workMacros = Substitute.For<IWorkMacroService>();
        _macroData = Substitute.For<IMacroDataProvider>();
        _workMacros.ProcessWorkMacroAsync(Arg.Any<Work>()).Returns(true);
        _workMacros.ProcessWorkChangeMacroAsync(Arg.Any<WorkChange>()).Returns(true);
        _macroCompilation = Substitute.For<IMacroCompilationService>();
        _logger = Substitute.For<ILogger<ContainerWorkChildrenManager>>();
        _manager = new ContainerWorkChildrenManager(
            _context, new EntityCollectionUpdateService(_context), _workMacros, _macroData, _macroCompilation, _logger);

        _parent = NewWork(null);
        _subWork = NewWork(_parent.Id);
        _parentChange = NewChange(_parent.Id);
        _subChange = NewChange(_subWork.Id);
        var deletedSubWork = NewWork(_parent.Id);
        deletedSubWork.IsDeleted = true;
        _context.Work.AddRange(_parent, _subWork, deletedSubWork);
        _context.WorkChange.AddRange(_parentChange, _subChange);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context?.Dispose();

    [Test]
    public async Task RunsTheMacros_InContainerOrder()
    {
        await _manager.RecalculateForNewOwnerAsync(_parent, CancellationToken.None);

        Received.InOrder(() =>
        {
            _workMacros.ProcessWorkMacroAsync(_subWork);
            _workMacros.ProcessWorkMacroAsync(_parent);
            _workMacros.ProcessWorkChangeMacroAsync(Arg.Is<WorkChange>(wc => wc.Id == _parentChange.Id || wc.Id == _subChange.Id));
            _workMacros.ProcessWorkChangeMacroAsync(Arg.Is<WorkChange>(wc => wc.Id == _parentChange.Id || wc.Id == _subChange.Id));
        });
        await _workMacros.Received(2).ProcessWorkMacroAsync(Arg.Any<Work>());
    }

    [Test]
    public async Task EverythingCalculated_ReportsSuccess()
        => (await _manager.RecalculateForNewOwnerAsync(_parent, CancellationToken.None)).ShouldBeTrue();

    [Test]
    public async Task AFailedSubWorkMacro_ReportsFailure()
    {
        _workMacros.ProcessWorkMacroAsync(_subWork).Returns(false);

        (await _manager.RecalculateForNewOwnerAsync(_parent, CancellationToken.None)).ShouldBeFalse();
    }

    [Test]
    public async Task AFailedWorkMacro_ReportsFailure()
    {
        _workMacros.ProcessWorkMacroAsync(_parent).Returns(false);

        (await _manager.RecalculateForNewOwnerAsync(_parent, CancellationToken.None)).ShouldBeFalse();
    }

    [Test]
    public async Task AFailedWorkChangeMacro_ReportsFailure()
    {
        _workMacros.ProcessWorkChangeMacroAsync(Arg.Is<WorkChange>(wc => wc.Id == _subChange.Id)).Returns(false);

        (await _manager.RecalculateForNewOwnerAsync(_parent, CancellationToken.None)).ShouldBeFalse();
    }

    [Test]
    public async Task AFailedUnpaidBreakMacro_ReportsFailure_AndLogsAnError()
    {
        await AddUnpaidSubBreakAsync();
        _macroData.GetMacroDataAsync(Arg.Any<Work>()).Returns(new MacroData());
        _macroCompilation.CompileAndExecuteAsync(Arg.Any<Guid>(), Arg.Any<MacroData>())
            .Returns(new MacroExecutionResult(false, null));

        (await _manager.RecalculateForNewOwnerAsync(_parent, CancellationToken.None)).ShouldBeFalse();

        _logger.ReceivedCalls().Count(call => call.GetArguments()[0] is LogLevel.Error).ShouldBe(1);
    }

    [Test]
    public async Task AnUnpaidSubBreak_IsJudgedForTheNewOwner_AndNegatesItsSurcharge()
    {
        var (subBreak, shift) = await AddUnpaidSubBreakAsync();

        _parent.ClientId = NewOwner;
        _macroData.GetMacroDataAsync(Arg.Any<Work>()).Returns(new MacroData());
        _macroCompilation.CompileAndExecuteAsync(shift.MacroId!.Value, Arg.Any<MacroData>())
            .Returns(new MacroExecutionResult(true, 2m, []));
        _workMacros.When(m => m.ProcessWorkMacroAsync(_parent)).Do(_ => _parent.Surcharges = 5m);

        await _manager.RecalculateForNewOwnerAsync(_parent, CancellationToken.None);

        await _macroData.Received(1).GetMacroDataAsync(Arg.Is<Work>(w => w.ClientId == NewOwner));
        subBreak.Surcharges.ShouldBe(-2m);
        _parent.Surcharges.ShouldBe(3m);
    }

    private async Task<(Break SubBreak, Shift Shift)> AddUnpaidSubBreakAsync()
    {
        var unpaid = new Absence { Id = Guid.NewGuid(), IsUnpaid = true };
        var subBreak = new Break
        {
            Id = Guid.NewGuid(),
            ParentWorkId = _parent.Id,
            ClientId = NewOwner,
            AbsenceId = unpaid.Id,
            CurrentDate = Day,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0)
        };
        var shift = new Shift { Id = _parent.ShiftId, MacroId = Guid.NewGuid() };
        _context.Absence.Add(unpaid);
        _context.Break.Add(subBreak);
        _context.Shift.Add(shift);
        await _context.SaveChangesAsync();
        return (subBreak, shift);
    }

    private static Work NewWork(Guid? parentId) => new()
    {
        Id = Guid.NewGuid(),
        ParentWorkId = parentId,
        ClientId = NewOwner,
        ShiftId = Guid.NewGuid(),
        CurrentDate = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0)
    };

    private static WorkChange NewChange(Guid workId) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = workId,
        Type = WorkChangeType.CorrectionEnd,
        ChangeTime = 1m
    };
}
