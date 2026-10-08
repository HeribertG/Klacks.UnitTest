// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-10-08 (K7): the period hours report the pay-period target prorated by company membership days,
/// like Wizard 1 does, so the schedule's target column, find_replacement, recovery and the target-drift trigger do not
/// rank a leaver against the full monthly target. Covers both read paths (PeriodHoursService and
/// WorkRepository.GetPeriodHoursForClients), with and without a cached ClientPeriodHours row.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class PeriodHoursMembershipProrationTests
{
    private const decimal MonthlyTarget = 124m;

    private static readonly DateOnly MarchStart = new(2026, 3, 1);
    private static readonly DateOnly MarchEnd = new(2026, 3, 31);

    private readonly Guid _leaver = Guid.NewGuid();
    private readonly Guid _joiner = Guid.NewGuid();
    private readonly Guid _fullMember = Guid.NewGuid();
    private readonly Guid _withoutMembership = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private PeriodHoursService _periodHoursService = null!;
    private WorkRepository _workRepository = null!;

    private List<Guid> AllClients => [_leaver, _joiner, _fullMember, _withoutMembership];

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _context.Membership.AddRange(
            Membership(_leaver, new DateTime(2020, 1, 1), new DateTime(2026, 3, 27)),
            Membership(_joiner, new DateTime(2026, 3, 10), null),
            Membership(_fullMember, new DateTime(2020, 1, 1), null));
        await _context.SaveChangesAsync();

        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(call => ((List<Guid>)call[0]).ToDictionary(id => id, _ => new EffectiveContractData { GuaranteedHours = MonthlyTarget }));
        contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { GuaranteedHours = MonthlyTarget });

        _periodHoursService = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contractDataProvider,
            Substitute.For<IWeekConfiguration>());

        _workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            contractDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task PeriodHoursService_ProratesTheTargetByMemberDays()
    {
        var result = await _periodHoursService.GetPeriodHoursAsync(AllClients, MarchStart, MarchEnd);

        AssertProrated(result);
    }

    [Test]
    public async Task PeriodHoursService_CachedRow_StillProratesTheTarget()
    {
        _context.ClientPeriodHours.AddRange(AllClients.Select(CacheRow));
        await _context.SaveChangesAsync();

        var result = await _periodHoursService.GetPeriodHoursAsync(AllClients, MarchStart, MarchEnd);

        AssertProrated(result);
    }

    [Test]
    public async Task PeriodHoursService_SingleClientCalculation_ProratesTheTarget()
    {
        var result = await _periodHoursService.CalculatePeriodHoursAsync(_leaver, MarchStart, MarchEnd);

        result.GuaranteedHours.ShouldBe(108m);
    }

    [Test]
    public async Task WorkRepository_FallbackPath_ProratesTheTargetByMemberDays()
    {
        var result = await _workRepository.GetPeriodHoursForClients(AllClients, MarchStart, MarchEnd);

        AssertProrated(result);
    }

    [Test]
    public async Task WorkRepository_CachedRow_StillProratesTheTarget()
    {
        _context.ClientPeriodHours.AddRange(AllClients.Select(CacheRow));
        await _context.SaveChangesAsync();

        var result = await _workRepository.GetPeriodHoursForClients(AllClients, MarchStart, MarchEnd);

        AssertProrated(result);
    }

    private void AssertProrated(IReadOnlyDictionary<Guid, Klacks.Api.Domain.DTOs.Schedules.PeriodHoursResource> result)
    {
        // Exit on 27.03.: 124 x 27/31 = 108. Entry on 10.03.: 124 x 22/31 = 88.
        result[_leaver].GuaranteedHours.ShouldBe(108m);
        result[_joiner].GuaranteedHours.ShouldBe(88m);
        result[_fullMember].GuaranteedHours.ShouldBe(MonthlyTarget);
        result[_withoutMembership].GuaranteedHours.ShouldBe(MonthlyTarget);
    }

    private static Membership Membership(Guid clientId, DateTime validFrom, DateTime? validUntil) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ValidFrom = validFrom,
        ValidUntil = validUntil,
    };

    private static ClientPeriodHours CacheRow(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        StartDate = MarchStart,
        EndDate = MarchEnd,
        Hours = 0m,
        Surcharges = 0m,
        AnalyseToken = null,
        CalculatedAt = DateTime.UtcNow,
    };
}
