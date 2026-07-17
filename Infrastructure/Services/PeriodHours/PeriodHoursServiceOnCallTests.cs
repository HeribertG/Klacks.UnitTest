// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the on-call contribution to PeriodHoursService.CalculatePeriodHoursAsync: presence and
/// standby work changes add their WEIGHTED ChangeTime (raw hours * factor) to the original client's
/// Hours, factor 0 and a disabled feature add nothing, on-call combines additively with a replacement on
/// the same work, and an over-midnight on-call window (stored as a raw duration ChangeTime) is weighted
/// exactly like any other.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class PeriodHoursServiceOnCallTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 3, 1);
    private static readonly DateOnly End = new(2026, 3, 31);
    private static readonly DateOnly WorkDay = new(2026, 3, 10);

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractDataProvider = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Presence_Factor100_AddsFullHours()
    {
        await SeedOnCallAsync(WorkChangeType.OnCallPresence, changeTime: 8m);
        var service = CreateService(new OnCallConfig(true, 1.0m, 0.0m, false));

        var result = await service.CalculatePeriodHoursAsync(ClientId, Start, End);

        result.Hours.ShouldBe(8m);
    }

    [Test]
    public async Task Standby_Factor25_AddsQuarterHours()
    {
        await SeedOnCallAsync(WorkChangeType.OnCallStandby, changeTime: 8m);
        var service = CreateService(new OnCallConfig(true, 1.0m, 0.25m, false));

        var result = await service.CalculatePeriodHoursAsync(ClientId, Start, End);

        result.Hours.ShouldBe(2m);
    }

    [Test]
    public async Task Standby_FactorZero_AddsNothing()
    {
        await SeedOnCallAsync(WorkChangeType.OnCallStandby, changeTime: 8m);
        var service = CreateService(new OnCallConfig(true, 1.0m, 0.0m, false));

        var result = await service.CalculatePeriodHoursAsync(ClientId, Start, End);

        result.Hours.ShouldBe(0m);
    }

    [Test]
    public async Task Presence_FeatureDisabled_AddsNothing()
    {
        await SeedOnCallAsync(WorkChangeType.OnCallPresence, changeTime: 8m);
        var service = CreateService(new OnCallConfig(false, 1.0m, 0.0m, false));

        var result = await service.CalculatePeriodHoursAsync(ClientId, Start, End);

        result.Hours.ShouldBe(0m);
    }

    [Test]
    public async Task OverMidnightWindow_WeightsRawDuration()
    {
        // A 22:00 -> 06:00 presence window is stored as ChangeTime 8 (raw duration, over-midnight handled
        // at write time by WorkMacroService); the read path weights the raw duration regardless of times.
        await SeedOnCallAsync(
            WorkChangeType.OnCallPresence, changeTime: 8m, start: new TimeOnly(22, 0), end: new TimeOnly(6, 0));
        var service = CreateService(new OnCallConfig(true, 0.5m, 0.0m, false));

        var result = await service.CalculatePeriodHoursAsync(ClientId, Start, End);

        result.Hours.ShouldBe(4m);
    }

    [Test]
    public async Task OnCallCombinesWithReplacementOnSameWork()
    {
        var work = SeedWork(workTime: 0m);
        // Presence 8h * 1.0 = 8; a replacement of 3h moves 3h off the original client.
        AddWorkChange(work, WorkChangeType.OnCallPresence, changeTime: 8m);
        AddWorkChange(work, WorkChangeType.ReplacementStart, changeTime: 3m, replaceClientId: Guid.NewGuid());
        await _context.SaveChangesAsync();

        var service = CreateService(new OnCallConfig(true, 1.0m, 0.0m, false));

        var result = await service.CalculatePeriodHoursAsync(ClientId, Start, End);

        result.Hours.ShouldBe(5m);
    }

    private async Task SeedOnCallAsync(
        WorkChangeType type, decimal changeTime, TimeOnly? start = null, TimeOnly? end = null)
    {
        var work = SeedWork(workTime: 0m);
        AddWorkChange(work, type, changeTime, start: start, end: end);
        await _context.SaveChangesAsync();
    }

    private Work SeedWork(decimal workTime)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = WorkDay,
            WorkTime = workTime,
            AnalyseToken = null,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        };
        _context.Work.Add(work);
        return work;
    }

    private void AddWorkChange(
        Work work, WorkChangeType type, decimal changeTime, Guid? replaceClientId = null,
        TimeOnly? start = null, TimeOnly? end = null)
    {
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = type,
            ChangeTime = changeTime,
            ReplaceClientId = replaceClientId,
            AnalyseToken = null,
            StartTime = start ?? new TimeOnly(20, 0),
            EndTime = end ?? new TimeOnly(22, 0),
        });
    }

    private PeriodHoursService CreateService(OnCallConfig onCallConfig)
    {
        var resolver = Substitute.For<IOnCallConfigResolver>();
        resolver.ResolveAsync().Returns(onCallConfig);

        return new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            _contractDataProvider,
            Substitute.For<IWeekConfiguration>(),
            resolver);
    }
}
