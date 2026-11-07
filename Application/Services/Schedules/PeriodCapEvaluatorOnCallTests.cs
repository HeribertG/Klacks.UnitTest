// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Wiring test for the TotalHours scope of PeriodCapEvaluator with the real PeriodHoursService on an in-memory
/// database: an on-call absence (Absence.IsOnCall) adds nothing to the cap baseline, so an on-call day with a short
/// call-out does not breach a cap that the call-out alone stays under. An ordinary absence of the same length still
/// counts and breaches it.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class PeriodCapEvaluatorOnCallTests
{
    private const decimal CapHours = 10m;
    private const decimal AbsenceHours = 8m;
    private const decimal CallOutHours = 4m;
    private const string ClientName = "Jane Doe";

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 3, 14);

    private DataBaseContext _context = null!;
    private PeriodCapEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var contracts = Substitute.For<IClientContractDataProvider>();
        contracts.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        contracts.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

        var periodHoursService = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contracts,
            Substitute.For<IWeekConfiguration>(),
            new FrozenPeriodPolicy(_context));

        var rules = Substitute.For<IPeriodCapRuleRepository>();
        rules.GetAllActiveAsync().Returns(new List<PeriodCapRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Period = PeriodCapPeriod.Month,
                Scope = PeriodCapScope.TotalHours,
                CapHours = CapHours,
                ImportSourceKey = "region-setup:compliance.periodCaps:month:totalhours",
                ImportContentHash = "irrelevant-for-this-test",
            },
        });

        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(ComplianceRuleNames.PeriodCap).Returns(RuleEnforcementMode.Warn);

        _evaluator = new PeriodCapEvaluator(
            rules,
            periodHoursService,
            enforcement,
            Substitute.For<IClientMembershipStartResolver>(),
            contracts,
            Substitute.For<IOvertimeConfigResolver>(),
            Substitute.For<IClientWorkHoursProvider>(),
            Substitute.For<IWeekConfiguration>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task OnCallDayWithACallOut_StaysUnderTheCap()
    {
        await SeedAbsenceDayWithCallOutAsync(isOnCall: true);

        var result = await _evaluator.EvaluateAsync(ClientId, ClientName, Day);

        result.ShouldBeEmpty("only the 4 h call-out counts, the 8 h on-call absence does not");
    }

    [Test]
    public async Task VacationDayWithAWork_StillBreachesTheCap()
    {
        await SeedAbsenceDayWithCallOutAsync(isOnCall: false);

        var result = await _evaluator.EvaluateAsync(ClientId, ClientName, Day);

        var entry = result.ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.Comment.ShouldBe(ScheduleValidationKeys.PeriodCap);
    }

    private async Task SeedAbsenceDayWithCallOutAsync(bool isOnCall)
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
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            AbsenceId = absence.Id,
            CurrentDate = Day,
            WorkTime = AbsenceHours,
            AnalyseToken = null,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = Day,
            WorkTime = CallOutHours,
            AnalyseToken = null,
            StartTime = new TimeOnly(20, 0),
            EndTime = new TimeOnly(0, 0),
        });
        await _context.SaveChangesAsync();
    }
}
