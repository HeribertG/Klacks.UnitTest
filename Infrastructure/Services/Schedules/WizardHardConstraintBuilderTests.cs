// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardHardConstraintBuilderTests
{
    private DataBaseContext _context = null!;
    private WizardHardConstraintBuilder _sut = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        _sut = new WizardHardConstraintBuilder(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task BuildAsync_MapsAllFourSectionsAndFiltersByAnalyseToken()
    {
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var shift = Guid.NewGuid();
        var from = new DateOnly(2026, 4, 20);
        var until = new DateOnly(2026, 4, 24);
        Guid? scenarioToken = Guid.NewGuid();

        _context.ScheduleCommands.Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(),
            ClientId = agentA,
            CurrentDate = new DateOnly(2026, 4, 21),
            CommandKeyword = "FREE",
            AnalyseToken = scenarioToken,
        });
        _context.ScheduleCommands.Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(),
            ClientId = agentA,
            CurrentDate = new DateOnly(2026, 4, 22),
            CommandKeyword = "EARLY",
            AnalyseToken = null,
        });

        _context.ClientShiftPreference.Add(new ClientShiftPreference
        {
            Id = Guid.NewGuid(),
            ClientId = agentA,
            ShiftId = shift,
            PreferenceType = ShiftPreferenceType.Preferred,
        });
        _context.ClientShiftPreference.Add(new ClientShiftPreference
        {
            Id = Guid.NewGuid(),
            ClientId = agentB,
            ShiftId = shift,
            PreferenceType = ShiftPreferenceType.Blacklist,
        });

        var absenceId = Guid.NewGuid();
        _context.Absence.Add(new Absence
        {
            Id = absenceId,
            Name = new MultiLanguage { De = "Urlaub" },
        });
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = agentA,
            CurrentDate = new DateOnly(2026, 4, 23),
            AbsenceId = absenceId,
            AnalyseToken = scenarioToken,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(17, 0),
        });

        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = agentA,
            CurrentDate = new DateOnly(2026, 4, 22),
            ShiftId = shift,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.Confirmed,
            AnalyseToken = scenarioToken,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = agentA,
            CurrentDate = new DateOnly(2026, 4, 22),
            ShiftId = shift,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
            AnalyseToken = scenarioToken,
        });

        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync(
            new[] { agentA, agentB }, from, until, scenarioToken, CancellationToken.None);

        result.ScheduleCommands.Count().ShouldBe(1);
        result.ScheduleCommands[0].Keyword.ShouldBe(ScheduleCommandKeyword.Free);

        result.ShiftPreferences.Count().ShouldBe(2);
        result.ShiftPreferences.Count(p => p.Kind == ShiftPreferenceKind.Preferred).ShouldBe(1);
        result.ShiftPreferences.Count(p => p.Kind == ShiftPreferenceKind.Blacklist).ShouldBe(1);

        result.BreakBlockers.Count().ShouldBe(1);
        result.BreakBlockers[0].Reason.ShouldBe("Urlaub");

        result.LockedWorks.Count().ShouldBe(1);
        result.LockedWorks[0].TotalHours.ShouldBe(8m);
    }

    [Test]
    public async Task BuildAsync_BreakBlocker_CarriesWorkTimeIntoHours()
    {
        var agent = Guid.NewGuid();
        var from = new DateOnly(2026, 4, 20);
        var until = new DateOnly(2026, 4, 24);

        var absenceId = Guid.NewGuid();
        _context.Absence.Add(new Absence
        {
            Id = absenceId,
            Name = new MultiLanguage { De = "Krank" },
        });
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            CurrentDate = new DateOnly(2026, 4, 21),
            AbsenceId = absenceId,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(17, 0),
            WorkTime = 8.5m,
            AnalyseToken = null,
        });
        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync(
            new[] { agent }, from, until, analyseToken: null, CancellationToken.None);

        result.BreakBlockers.Count().ShouldBe(1);
        result.BreakBlockers[0].Hours.ShouldBe(8.5m);
        result.BreakBlockers[0].FromInclusive.ShouldBe(new DateOnly(2026, 4, 21));
        result.BreakBlockers[0].UntilInclusive.ShouldBe(new DateOnly(2026, 4, 21));
        result.BreakBlockers[0].Reason.ShouldBe("Krank");
    }

    [Test]
    public async Task BuildAsync_FiltersScheduleCommandsForMainScenario_WhenAnalyseTokenIsNull()
    {
        var agent = Guid.NewGuid();
        var from = new DateOnly(2026, 4, 20);
        var until = new DateOnly(2026, 4, 22);

        _context.ScheduleCommands.Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            CurrentDate = new DateOnly(2026, 4, 21),
            CommandKeyword = "FREE",
            AnalyseToken = null,
        });
        _context.ScheduleCommands.Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            CurrentDate = new DateOnly(2026, 4, 21),
            CommandKeyword = "LATE",
            AnalyseToken = Guid.NewGuid(),
        });
        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync(
            new[] { agent }, from, until, analyseToken: null, CancellationToken.None);

        result.ScheduleCommands.Count().ShouldBe(1);
        result.ScheduleCommands[0].Keyword.ShouldBe(ScheduleCommandKeyword.Free);
    }
}
