// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ClientOnCallHoursProvider: it sums weighted on-call hours over a date range (matched on the
/// parent Work's CurrentDate), isolates scenarios by the parent Work's AnalyseToken, excludes
/// soft-deleted rows and returns zero when the config is disabled.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientOnCallHoursProviderTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly OnCallConfig EnabledConfig = new(true, 1.0m, 0.25m, false);

    private DataBaseContext _context = null!;
    private ClientOnCallHoursProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _provider = new ClientOnCallHoursProvider(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task SumsWeightedHours_PresencePlusStandby()
    {
        AddOnCall(new DateOnly(2026, 3, 10), WorkChangeType.OnCallPresence, 8m);
        AddOnCall(new DateOnly(2026, 3, 12), WorkChangeType.OnCallStandby, 8m);
        await _context.SaveChangesAsync();

        var hours = await _provider.GetWeightedOnCallHoursAsync(
            ClientId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, EnabledConfig);

        // 8 * 1.0 + 8 * 0.25 = 10
        hours.ShouldBe(10m);
    }

    [Test]
    public async Task ExcludesRowsOutsideTheWindow()
    {
        AddOnCall(new DateOnly(2026, 2, 28), WorkChangeType.OnCallPresence, 8m);
        AddOnCall(new DateOnly(2026, 4, 1), WorkChangeType.OnCallPresence, 8m);
        AddOnCall(new DateOnly(2026, 3, 1), WorkChangeType.OnCallPresence, 5m);
        AddOnCall(new DateOnly(2026, 3, 31), WorkChangeType.OnCallPresence, 3m);
        await _context.SaveChangesAsync();

        var hours = await _provider.GetWeightedOnCallHoursAsync(
            ClientId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, EnabledConfig);

        // Only the inclusive-boundary rows count: 5 + 3 = 8.
        hours.ShouldBe(8m);
    }

    [Test]
    public async Task IsolatesScenarioByParentWorkToken()
    {
        var scenarioToken = Guid.NewGuid();
        AddOnCall(new DateOnly(2026, 3, 10), WorkChangeType.OnCallPresence, 8m, analyseToken: null);
        AddOnCall(new DateOnly(2026, 3, 11), WorkChangeType.OnCallPresence, 4m, analyseToken: scenarioToken);
        await _context.SaveChangesAsync();

        var realHours = await _provider.GetWeightedOnCallHoursAsync(
            ClientId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, EnabledConfig);
        var scenarioHours = await _provider.GetWeightedOnCallHoursAsync(
            ClientId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), scenarioToken, EnabledConfig);

        realHours.ShouldBe(8m);
        scenarioHours.ShouldBe(4m);
    }

    [Test]
    public async Task ExcludesSoftDeletedWorkChanges()
    {
        AddOnCall(new DateOnly(2026, 3, 10), WorkChangeType.OnCallPresence, 8m, isDeleted: true);
        await _context.SaveChangesAsync();

        var hours = await _provider.GetWeightedOnCallHoursAsync(
            ClientId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, EnabledConfig);

        hours.ShouldBe(0m);
    }

    [Test]
    public async Task Disabled_ReturnsZeroWithoutQuerying()
    {
        AddOnCall(new DateOnly(2026, 3, 10), WorkChangeType.OnCallPresence, 8m);
        await _context.SaveChangesAsync();

        var hours = await _provider.GetWeightedOnCallHoursAsync(
            ClientId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, new OnCallConfig(false, 1.0m, 0.25m, false));

        hours.ShouldBe(0m);
    }

    private void AddOnCall(
        DateOnly date, WorkChangeType type, decimal changeTime,
        Guid? analyseToken = null, bool isDeleted = false)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = date,
            AnalyseToken = analyseToken,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        };
        _context.Work.Add(work);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = type,
            ChangeTime = changeTime,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
            StartTime = new TimeOnly(20, 0),
            EndTime = new TimeOnly(22, 0),
        });
    }
}
