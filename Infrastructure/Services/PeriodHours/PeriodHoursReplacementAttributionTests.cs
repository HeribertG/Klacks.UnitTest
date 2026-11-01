// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Period-hours attribution of replacements on both read paths: PeriodHoursService (the cache-populating calculation)
/// and WorkRepository.GetPeriodHoursForClients (the cache-miss fallback behind the schedule row header). A replacement of
/// any type takes ChangeTime from the employee who owns the work and gives it to the substitute, on the period of the
/// work's own date. ReplacementWithin is what the recovery proposal writes, so it must not differ from a manual
/// ReplacementStart/End. No ClientPeriodHours row exists unless a test writes one.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class PeriodHoursReplacementAttributionTests
{
    private const decimal WorkHours = 8m;
    private const decimal HandedOverHours = 6m;
    private const decimal RemainingHours = WorkHours - HandedOverHours;
    private const decimal SubstituteOwnHours = 5m;
    private const decimal WorkSurcharges = 2m;
    private const decimal HandedOverSurcharges = 1.5m;
    private const decimal RemainingSurcharges = WorkSurcharges - HandedOverSurcharges;
    private const decimal AbsenceCreditHours = 7.11m;

    private static readonly Guid Original = Guid.NewGuid();
    private static readonly Guid Replacer = Guid.NewGuid();
    private static readonly List<Guid> Both = [Original, Replacer];
    private static readonly DateOnly MarchStart = new(2026, 3, 1);
    private static readonly DateOnly MarchEnd = new(2026, 3, 31);
    private static readonly DateOnly AprilStart = new(2026, 4, 1);
    private static readonly DateOnly AprilEnd = new(2026, 4, 30);
    private static readonly DateOnly WorkDay = new(2026, 3, 10);

    private DataBaseContext _context = null!;
    private PeriodHoursService _service = null!;
    private WorkRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());
        contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());

        _service = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contractDataProvider,
            Substitute.For<IWeekConfiguration>());

        _repository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            contractDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task Replacement_MovesChangeTimeToTheReplacer_OnBothPaths(WorkChangeType type)
    {
        await SeedWorkWithReplacementAsync(type, WorkDay, new TimeOnly(8, 0), new TimeOnly(16, 0), new TimeOnly(9, 0), new TimeOnly(15, 0));

        await AssertBothPathsAsync(MarchStart, MarchEnd, RemainingHours, HandedOverHours);
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task Fallback_ReadForTheSubstituteAlone_StillFindsTheReplacement(WorkChangeType type)
    {
        await SeedWorkWithReplacementAsync(type, WorkDay, new TimeOnly(8, 0), new TimeOnly(16, 0), new TimeOnly(9, 0), new TimeOnly(15, 0));

        var header = await _repository.GetPeriodHoursForClients([Replacer], MarchStart, MarchEnd);

        header[Replacer].Hours.ShouldBe(HandedOverHours);
    }

    [Test]
    public async Task ReplacementWithin_MovesItsSurchargesToTheSubstitute()
    {
        await SeedWorkWithReplacementAsync(
            WorkChangeType.ReplacementWithin, WorkDay, new TimeOnly(8, 0), new TimeOnly(16, 0), new TimeOnly(9, 0), new TimeOnly(15, 0));

        var hours = await _service.GetPeriodHoursAsync(Both, MarchStart, MarchEnd);

        hours[Original].Surcharges.ShouldBe(RemainingSurcharges);
        hours[Replacer].Surcharges.ShouldBe(HandedOverSurcharges);
    }

    [Test]
    public async Task ReplacementWithin_AfterMidnight_StaysInThePeriodOfTheWorkDate()
    {
        await SeedWorkWithReplacementAsync(
            WorkChangeType.ReplacementWithin, MarchEnd, new TimeOnly(23, 0), new TimeOnly(7, 0), new TimeOnly(1, 0), new TimeOnly(7, 0));

        await AssertBothPathsAsync(MarchStart, MarchEnd, RemainingHours, HandedOverHours);
        await AssertBothPathsAsync(AprilStart, AprilEnd, 0m, 0m);
    }

    [Test]
    public async Task ReplacementWithin_AddsToTheSubstitutesOwnWork()
    {
        await SeedWorkWithReplacementAsync(
            WorkChangeType.ReplacementWithin, WorkDay, new TimeOnly(8, 0), new TimeOnly(16, 0), new TimeOnly(9, 0), new TimeOnly(15, 0));
        _context.Work.Add(NewWork(Replacer, WorkDay.AddDays(1), SubstituteOwnHours, new TimeOnly(8, 0), new TimeOnly(13, 0)));
        await _context.SaveChangesAsync();

        await AssertBothPathsAsync(MarchStart, MarchEnd, RemainingHours, SubstituteOwnHours + HandedOverHours);
    }

    [Test]
    public async Task AbsenceCover_LeavesTheAbsentEmployeeTheCreditInsteadOfCreditPlusWork()
    {
        await SeedWorkWithReplacementAsync(
            WorkChangeType.ReplacementWithin, WorkDay, new TimeOnly(8, 0), new TimeOnly(16, 0), new TimeOnly(8, 0), new TimeOnly(16, 0), WorkHours);
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Original,
            CurrentDate = WorkDay,
            WorkTime = AbsenceCreditHours,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
        });
        await _context.SaveChangesAsync();

        await AssertBothPathsAsync(MarchStart, MarchEnd, AbsenceCreditHours, WorkHours);
    }

    [Test]
    public async Task ReplacementWithin_OnAScenarioWork_MovesHoursOnlyInThatScenario()
    {
        var token = Guid.NewGuid();
        await SeedWorkWithReplacementAsync(
            WorkChangeType.ReplacementWithin, WorkDay, new TimeOnly(8, 0), new TimeOnly(16, 0), new TimeOnly(9, 0), new TimeOnly(15, 0), token: token);

        await AssertBothPathsAsync(MarchStart, MarchEnd, RemainingHours, HandedOverHours, token);
        await AssertBothPathsAsync(MarchStart, MarchEnd, 0m, 0m);
    }

    [Test]
    public async Task Recalculate_WritesTheReplacementIntoTheCacheRowTheRowHeaderReads()
    {
        await SeedWorkWithReplacementAsync(
            WorkChangeType.ReplacementWithin, WorkDay, new TimeOnly(8, 0), new TimeOnly(16, 0), new TimeOnly(9, 0), new TimeOnly(15, 0));
        _context.Client.AddRange(
            new Client { Id = Original, Name = "Dubach", FirstName = "Elsa" },
            new Client { Id = Replacer, Name = "Gerber", FirstName = "Zoe" });
        await _context.SaveChangesAsync();

        await _service.RecalculatePeriodHoursAsync(Original, MarchStart, MarchEnd);
        await _service.RecalculatePeriodHoursAsync(Replacer, MarchStart, MarchEnd);

        (await _context.ClientPeriodHours.CountAsync()).ShouldBe(Both.Count);
        var header = await _repository.GetPeriodHoursForClients(Both, MarchStart, MarchEnd);
        header[Original].Hours.ShouldBe(RemainingHours);
        header[Replacer].Hours.ShouldBe(HandedOverHours);
    }

    private async Task SeedWorkWithReplacementAsync(
        WorkChangeType type,
        DateOnly date,
        TimeOnly workStart,
        TimeOnly workEnd,
        TimeOnly changeStart,
        TimeOnly changeEnd,
        decimal changeTime = HandedOverHours,
        Guid? token = null)
    {
        var work = NewWork(Original, date, WorkHours, workStart, workEnd, token);
        _context.Work.Add(work);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = type,
            ChangeTime = changeTime,
            Surcharges = HandedOverSurcharges,
            StartTime = changeStart,
            EndTime = changeEnd,
            ReplaceClientId = Replacer,
            AnalyseToken = token,
        });
        await _context.SaveChangesAsync();
    }

    private static Work NewWork(Guid clientId, DateOnly date, decimal hours, TimeOnly start, TimeOnly end, Guid? token = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = date,
        WorkTime = hours,
        Surcharges = WorkSurcharges,
        StartTime = start,
        EndTime = end,
        AnalyseToken = token,
    };

    private async Task AssertBothPathsAsync(
        DateOnly start, DateOnly end, decimal expectedOriginal, decimal expectedSubstitute, Guid? token = null)
    {
        var service = await _service.GetPeriodHoursAsync(Both, start, end, token);
        var fallback = await _repository.GetPeriodHoursForClients(Both, start, end, token);

        service[Original].Hours.ShouldBe(expectedOriginal);
        service[Replacer].Hours.ShouldBe(expectedSubstitute);
        fallback[Original].Hours.ShouldBe(expectedOriginal);
        fallback[Replacer].Hours.ShouldBe(expectedSubstitute);
    }
}
