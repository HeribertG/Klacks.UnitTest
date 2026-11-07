// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-11-07: an on-call absence (Absence.IsOnCall) stands for on-call duty outside the company and
/// adds neither hours nor surcharges to the period hours; only the actual assignment on that day counts. Checked on
/// both period-hours entry points that sum breaks live (PeriodHoursService, cache-populating, and the cache-miss
/// fallback of WorkRepository.GetPeriodHoursForClients) and on the persisted row. An ordinary absence keeps counting,
/// a soft-deleted on-call type keeps its on-call meaning, and a container sub-break keeps counting whatever its type,
/// because it is part of the container's time.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class PeriodHoursOnCallBreakTests
{
    private const decimal OnCallHours = 8m;
    private const decimal OnCallSurcharges = 2m;
    private const decimal CallOutHours = 4m;
    private const decimal CallOutSurcharges = 1m;
    private const decimal VacationHours = 8.4m;
    private const decimal VacationSurcharges = 0.5m;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 3, 1);
    private static readonly DateOnly End = new(2026, 3, 31);
    private static readonly DateOnly OnCallDay = new(2026, 3, 14);

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task OnCallBreakAndCallOutOnTheSameDay_CountOnlyTheCallOut_OnBothPaths()
    {
        var onCall = AddAbsence(isOnCall: true);
        AddBreak(onCall.Id, OnCallDay, OnCallHours, OnCallSurcharges);
        AddWork(OnCallDay, CallOutHours, CallOutSurcharges);
        await _context.SaveChangesAsync();

        foreach (var result in await BothPathsAsync())
        {
            result[ClientId].Hours.ShouldBe(CallOutHours);
            result[ClientId].Surcharges.ShouldBe(CallOutSurcharges);
        }
    }

    [Test]
    public async Task VacationNextToAnOnCallBreak_KeepsCounting_OnBothPaths()
    {
        var onCall = AddAbsence(isOnCall: true);
        var vacation = AddAbsence(isOnCall: false);
        AddBreak(onCall.Id, OnCallDay, OnCallHours, OnCallSurcharges);
        AddBreak(vacation.Id, OnCallDay.AddDays(1), VacationHours, VacationSurcharges);
        AddWork(OnCallDay, CallOutHours, CallOutSurcharges);
        await _context.SaveChangesAsync();

        foreach (var result in await BothPathsAsync())
        {
            result[ClientId].Hours.ShouldBe(CallOutHours + VacationHours);
            result[ClientId].Surcharges.ShouldBe(CallOutSurcharges + VacationSurcharges);
        }
    }

    [Test]
    public async Task OnCallBreakOfASoftDeletedType_StillCountsNothing()
    {
        var onCall = AddAbsence(isOnCall: true);
        AddBreak(onCall.Id, OnCallDay, OnCallHours, OnCallSurcharges);
        onCall.IsDeleted = true;
        await _context.SaveChangesAsync();

        foreach (var result in await BothPathsAsync())
        {
            result[ClientId].Hours.ShouldBe(0m);
            result[ClientId].Surcharges.ShouldBe(0m);
        }
    }

    [Test]
    public async Task OnCallSubBreakInsideAContainer_KeepsItsDeduction()
    {
        const decimal containerHours = 8m;
        const decimal unpaidSpanHours = -2m;
        var onCall = AddAbsence(isOnCall: true);
        var container = AddWork(OnCallDay, containerHours, 0m);
        var subBreak = AddBreak(onCall.Id, OnCallDay, unpaidSpanHours, 0m);
        subBreak.ParentWorkId = container.Id;
        await _context.SaveChangesAsync();

        foreach (var result in await BothPathsAsync())
        {
            result[ClientId].Hours.ShouldBe(containerHours + unpaidSpanHours);
        }
    }

    [Test]
    public async Task RecalculatePeriodHours_PersistsOnlyTheCallOut()
    {
        var onCall = AddAbsence(isOnCall: true);
        AddBreak(onCall.Id, OnCallDay, OnCallHours, OnCallSurcharges);
        AddWork(OnCallDay, CallOutHours, CallOutSurcharges);
        await _context.SaveChangesAsync();

        await CreatePeriodHoursService().RecalculatePeriodHoursAsync(ClientId, Start, End);

        var row = await _context.ClientPeriodHours.IgnoreQueryFilters().SingleAsync(p => p.ClientId == ClientId);
        row.Hours.ShouldBe(CallOutHours);
        row.Surcharges.ShouldBe(CallOutSurcharges);
    }

    private async Task<IReadOnlyList<Dictionary<Guid, Klacks.Api.Domain.DTOs.Schedules.PeriodHoursResource>>> BothPathsAsync()
    {
        var clientIds = new List<Guid> { ClientId };
        var core = await CreatePeriodHoursService().GetPeriodHoursAsync(clientIds, Start, End);
        var fallback = await CreateWorkRepository().GetPeriodHoursForClients(clientIds, Start, End);
        return [core, fallback];
    }

    private Absence AddAbsence(bool isOnCall)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            IsOnCall = isOnCall,
            Name = new MultiLanguage { De = isOnCall ? "Pikett" : "Ferien" },
            Abbreviation = new MultiLanguage(),
            Description = new MultiLanguage(),
        };
        _context.Absence.Add(absence);
        return absence;
    }

    private Break AddBreak(Guid absenceId, DateOnly day, decimal workTime, decimal surcharges)
    {
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            AbsenceId = absenceId,
            CurrentDate = day,
            WorkTime = workTime,
            Surcharges = surcharges,
            AnalyseToken = null,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(0, 0),
        };
        _context.Break.Add(breakEntry);
        return breakEntry;
    }

    private Work AddWork(DateOnly day, decimal workTime, decimal surcharges)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = day,
            WorkTime = workTime,
            Surcharges = surcharges,
            AnalyseToken = null,
            StartTime = new TimeOnly(20, 0),
            EndTime = new TimeOnly(0, 0),
        };
        _context.Work.Add(work);
        return work;
    }

    private static IClientContractDataProvider NoContracts()
    {
        var provider = Substitute.For<IClientContractDataProvider>();
        provider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());
        provider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        return provider;
    }

    private PeriodHoursService CreatePeriodHoursService() => new(
        _context,
        Substitute.For<ILogger<PeriodHoursService>>(),
        Substitute.For<IWorkNotificationService>(),
        Substitute.For<IClientGroupFilterService>(),
        NoContracts(),
        Substitute.For<IWeekConfiguration>(),
        new FrozenPeriodPolicy(_context));

    private WorkRepository CreateWorkRepository() => new(
        _context,
        Substitute.For<ILogger<Work>>(),
        Substitute.For<IClientBaseQueryService>(),
        Substitute.For<IWorkMacroService>(),
        NoContracts());
}
