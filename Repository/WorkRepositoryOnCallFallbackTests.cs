// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves the WorkRepository.GetPeriodHoursForClients cache-miss fallback attributes weighted on-call
/// hours identically to PeriodHoursService (which populates the cached path): a client with no cached
/// ClientPeriodHours row gets its Hours computed from Work + on-call work changes weighted by the
/// WORKTIME_ONCALL_* settings, so the same client can never show different Hours depending on cache warmth.
/// </summary>

namespace Klacks.UnitTest.Repository;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class WorkRepositoryOnCallFallbackTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 3, 1);
    private static readonly DateOnly End = new(2026, 3, 31);

    private DataBaseContext _context = null!;
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

        _repository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            contractDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Fallback_IncludesWeightedOnCallHours()
    {
        SeedOnCallSettings(enabled: true, presencePercent: 100, standbyPercent: 25);

        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = new DateOnly(2026, 3, 10),
            WorkTime = 5m,
            AnalyseToken = null,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(13, 0),
        };
        _context.Work.Add(work);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = WorkChangeType.OnCallPresence,
            ChangeTime = 8m,
            AnalyseToken = null,
            StartTime = new TimeOnly(20, 0),
            EndTime = new TimeOnly(4, 0),
        });
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = WorkChangeType.OnCallStandby,
            ChangeTime = 8m,
            AnalyseToken = null,
            StartTime = new TimeOnly(4, 0),
            EndTime = new TimeOnly(12, 0),
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetPeriodHoursForClients(new List<Guid> { ClientId }, Start, End);

        // 5 (work) + 8 * 1.0 (presence) + 8 * 0.25 (standby) = 15.
        result[ClientId].Hours.ShouldBe(15m);
    }

    private void SeedOnCallSettings(bool enabled, int presencePercent, int standbyPercent)
    {
        _context.Settings.Add(new SettingsModel { Type = SettingKeys.WorktimeOnCallEnabled, Value = enabled ? "true" : "false" });
        _context.Settings.Add(new SettingsModel { Type = SettingKeys.WorktimeOnCallPresenceCountsPercent, Value = presencePercent.ToString() });
        _context.Settings.Add(new SettingsModel { Type = SettingKeys.WorktimeOnCallStandbyCountsPercent, Value = standbyPercent.ToString() });
        _context.SaveChanges();
    }
}
