// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for OvertimeCascadeService wired to the REAL OvertimeSurchargeCalculator (in-memory DB): the fix
/// lives in GetSuccessorWorksAsync, so mocking the calculator would make the coverage vacuous. Reproduces
/// the Phase-2 review scenario where a dated revision switches the overtime basis (day -> week) mid-period
/// and an edit to a Work BEFORE the revision's Stichtag must reprocess a same-week successor AFTER it.
/// </summary>
using System.Collections.Generic;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class OvertimeCascadeServiceTests
{
    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private IWorkMacroService _workMacroService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private List<Work> _reprocessed = null!;
    private OvertimeCascadeService _sut = null!;

    private readonly DateOnly _monday = new(2027, 3, 1);
    private readonly DateOnly _revisionStichtag = new(2027, 3, 3);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>()).Returns(_monday);

        var calculator = new OvertimeSurchargeCalculator(
            _context,
            new OvertimeConfigResolver(_context, _contractDataProvider),
            _weekConfiguration);

        _reprocessed = new List<Work>();
        _workMacroService = Substitute.For<IWorkMacroService>();
        _workMacroService.ProcessWorkMacroAsync(Arg.Any<Work>())
            .Returns(Task.CompletedTask)
            .AndDoes(callInfo => _reprocessed.Add(callInfo.Arg<Work>()));

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.CompleteAsync().Returns(Task.CompletedTask);

        _sut = new OvertimeCascadeService(calculator, _workMacroService, _unitOfWork, NullLogger<OvertimeCascadeService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    // Review scenario: base rule = day-basis overtime, a revision from Wednesday switches to week basis.
    // The Monday anchor (before the Stichtag) resolves the day-basis ladder, so its own successor window
    // would be a single day and would MISS the Thursday sibling that already sits on the week basis. The
    // widened search must find and reprocess it; a Work in the following week must stay untouched.
    [Test]
    public async Task ReprocessSuccessors_RevisionSwitchesBasisMidPeriod_ReprocessesLaterSameWeekSuccessor()
    {
        var clientId = Guid.NewGuid();
        var ruleId = await SeedRuleWithBaseDayOvertimeAsync(clientId);
        await AddWeekBasisOvertimeRevisionAsync(ruleId, _revisionStichtag);

        var successorThursday = await AddWorkAsync(clientId, _monday.AddDays(3), workTime: 8m);
        var nextWeekControl = await AddWorkAsync(clientId, _monday.AddDays(7), workTime: 8m);

        var anchorMonday = BuildWork(clientId, _monday, workTime: 8m);

        await _sut.ReprocessSuccessorsAsync(anchorMonday);

        _reprocessed.Select(w => w.Id).ShouldContain(successorThursday.Id);
        _reprocessed.Select(w => w.Id).ShouldNotContain(nextWeekControl.Id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    private async Task<Guid> SeedRuleWithBaseDayOvertimeAsync(Guid clientId)
    {
        var ruleId = Guid.NewGuid();
        _context.SchedulingRules.Add(new SchedulingRule
        {
            Id = ruleId,
            Name = "Industry preset",
            OvertimeBasis = OvertimeBasis.Day,
            OvertimeTier1AfterHours = 8m,
            OvertimeTier1Rate = 0.5m,
        });
        await _context.SaveChangesAsync();
        _contractDataProvider.GetEffectiveContractDataAsync(clientId, Arg.Any<DateOnly>())
            .Returns(new EffectiveContractData { SchedulingRuleId = ruleId });
        return ruleId;
    }

    private async Task AddWeekBasisOvertimeRevisionAsync(Guid ruleId, DateOnly validFrom)
    {
        _context.SchedulingRuleRateRevisions.Add(new SchedulingRuleRateRevision
        {
            Id = Guid.NewGuid(),
            SchedulingRuleId = ruleId,
            ValidFrom = validFrom,
            NightRate = 0.5m,
            OvertimeBasis = OvertimeBasis.Week,
            OvertimeTier1AfterHours = 40m,
            OvertimeTier1Rate = 0.25m,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<Work> AddWorkAsync(Guid clientId, DateOnly date, decimal workTime)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = date,
            WorkTime = workTime,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(6, 0).AddHours((double)workTime),
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work;
    }

    private static Work BuildWork(Guid clientId, DateOnly date, decimal workTime) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = date,
        WorkTime = workTime,
        StartTime = new TimeOnly(6, 0),
        EndTime = new TimeOnly(6, 0).AddHours((double)workTime),
    };
}
