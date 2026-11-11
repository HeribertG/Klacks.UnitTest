// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Wizard 1 (WizardContextBuilder) and Wizard 2/3 (HarmonizerContextBuilder) produce the same automatic row order from the
/// same contract data and memberships: weekly and monthly contracts compared on the planned period, and an entry inside
/// the period counted by its member days only (owner decision 2026-11-10). Stand-ins are left out because the two stages
/// read the stand-in flag from different sources. Both stages also take the weekly cap from the same contract basis
/// (PlanningContractBasis) when a contract starts mid-period: the first-day and range stubs then deliver different data.
/// </summary>
[TestFixture]
public class RosterOrderStageParityTests
{
    private static readonly DateOnly WeekStart = new(2026, 1, 5);
    private static readonly DateOnly WeekEnd = new(2026, 1, 11);

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractProvider = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _contractProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { MinPauseHours = 11, MaxDailyHours = 10, MaxConsecutiveDays = 6, MaxWeeklyHours = 50 });
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task BothStages_RankWeeklyMonthlyAndMidPeriodJoinerAlike()
    {
        var monthly100 = Guid.NewGuid();
        var joiner160 = Guid.NewGuid();
        var weekly40 = Guid.NewGuid();
        var noGuarantee = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [monthly100] = Data(PaymentInterval.Monthly, guaranteed: 100m, fullTime: 180m),
            [joiner160] = Data(PaymentInterval.Monthly, guaranteed: 160m, fullTime: 180m),
            [weekly40] = Data(PaymentInterval.Weekly, guaranteed: 40m, fullTime: 42m),
            [noGuarantee] = Data(PaymentInterval.Weekly, guaranteed: 0m, fullTime: 42m),
        });
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = joiner160,
            ValidFrom = WeekEnd.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });
        _context.SaveChanges();
        IReadOnlyList<Guid> agentIds = [noGuarantee, joiner160, monthly100, weekly40];

        var wizard1 = await BuildWizardSut().BuildContextAsync(
            new WizardContextRequest(WeekStart, WeekEnd, agentIds, null, null), CancellationToken.None);
        var harmonizer = await BuildHarmonizerSut().BuildContextAsync(
            new HarmonizerContextRequest(WeekStart, WeekEnd, agentIds, AnalyseToken: null), CancellationToken.None);

        var expected = new[] { weekly40, monthly100, joiner160, noGuarantee }.Select(id => id.ToString()).ToList();
        wizard1.Agents.Select(a => a.Id).ShouldBe(expected);
        harmonizer.Agents.Select(a => a.Id).ShouldBe(expected);
    }

    [Test]
    public async Task BothStages_TakeTheWeeklyCapFromTheContractStartingMidPeriod()
    {
        var agent = Guid.NewGuid();
        var contractStart = WeekStart.AddDays(2);
        var settingsDefaults = new EffectiveContractData
        {
            HasActiveContract = false,
            MaxWeeklyHours = 45,
            MaxDailyHours = 10,
            MinPauseHours = 11,
            MaxConsecutiveDays = 6,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
        };
        var contract = Data(PaymentInterval.Weekly, guaranteed: 30m, fullTime: 42m) with { MaxWeeklyHours = 30 };
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData> { [agent] = settingsDefaults });
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
                for (var date = ci.ArgAt<DateOnly>(1); date <= ci.ArgAt<DateOnly>(2); date = date.AddDays(1))
                {
                    range[date] = new Dictionary<Guid, EffectiveContractData> { [agent] = date < contractStart ? settingsDefaults : contract };
                }

                return range;
            });
        _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = agent.ToString() });
        _context.SaveChanges();
        IReadOnlyList<Guid> agentIds = [agent];

        var wizard1 = await BuildWizardSut().BuildContextAsync(
            new WizardContextRequest(WeekStart, WeekEnd, agentIds, null, null), CancellationToken.None);
        var harmonizer = await BuildHarmonizerSut().BuildContextAsync(
            new HarmonizerContextRequest(WeekStart, WeekEnd, agentIds, AnalyseToken: null), CancellationToken.None);

        wizard1.Agents.Single().MaxWeeklyHours.ShouldBe(30d);
        ((double)harmonizer.Agents.Single().MaxWeeklyHours).ShouldBe(wizard1.Agents.Single().MaxWeeklyHours);
    }

    private static EffectiveContractData Data(PaymentInterval interval, decimal guaranteed, decimal fullTime) => new()
    {
        HasActiveContract = true,
        ContractId = Guid.NewGuid(),
        PaymentInterval = (int)interval,
        GuaranteedHours = guaranteed,
        FullTime = fullTime,
        MaxDailyHours = 10,
        MaxWeeklyHours = 50,
        MinPauseHours = 11,
        MaxConsecutiveDays = 6,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
    };

    private void Stub(Dictionary<Guid, EffectiveContractData> dataByAgent)
    {
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(dataByAgent);
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
                for (var date = ci.ArgAt<DateOnly>(1); date <= ci.ArgAt<DateOnly>(2); date = date.AddDays(1))
                {
                    range[date] = new Dictionary<Guid, EffectiveContractData>(dataByAgent);
                }

                return range;
            });

        foreach (var agent in dataByAgent.Keys)
        {
            _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = agent.ToString() });
        }
        _context.SaveChanges();
    }

    private WizardContextBuilder BuildWizardSut()
    {
        var shiftBuilder = Substitute.For<IWizardShiftBuilder>();
        shiftBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CoreShift>());
        var hardBuilder = Substitute.For<IWizardHardConstraintBuilder>();
        hardBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<DateOnly?>())
            .Returns(new HardConstraintResult([], [], [], [], []));
        var periodHours = Substitute.For<IPeriodHoursService>();
        periodHours
            .GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns(ci => (new DateOnly(ci.Arg<DateOnly>().Year, ci.Arg<DateOnly>().Month, 1),
                new DateOnly(ci.Arg<DateOnly>().Year, ci.Arg<DateOnly>().Month, DateTime.DaysInMonth(ci.Arg<DateOnly>().Year, ci.Arg<DateOnly>().Month))));
        periodHours
            .GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>());
        var eligibility = Substitute.For<IEligibilityMatrixBuilder>();
        eligibility
            .BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
                Arg.Any<CancellationToken>())
            .Returns(EligibilityMatrix.Empty);
        var availability = Substitute.For<IAvailabilityIneligibilityService>();
        availability
            .GetAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<AvailabilityShiftSlot>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<(string, Guid, DateOnly)>)new HashSet<(string, Guid, DateOnly)>());
        var warmStart = Substitute.For<IWizardWarmStartBuilder>();
        warmStart
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreWarmStartAssignment>)new List<CoreWarmStartAssignment>());
        var windows = Substitute.For<IWizardRestrictedWindowBuilder>();
        windows
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreRestrictedTimeWindow>)new List<CoreRestrictedTimeWindow>());
        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository
            .GetStandInIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        var agentBuilder = new WizardAgentSnapshotBuilder(_contractProvider, new MembershipWindowReader(_context));
        return new WizardContextBuilder(
            agentBuilder, shiftBuilder, hardBuilder, periodHours, _contractProvider, eligibility, availability, warmStart, windows, clientRepository);
    }

    private HarmonizerContextBuilder BuildHarmonizerSut()
    {
        var softening = Substitute.For<IWorkSofteningRepository>();
        softening.LoadAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkSoftening>>([]));

        var eligibility = Substitute.For<IEligibilityMatrixBuilder>();
        eligibility.BuildAsync(default!, default!, default!, default).ReturnsForAnyArgs(new EligibilityMatrix
        {
            Ineligible = new HashSet<(string, Guid, DateOnly)>(),
            Gaps = new Dictionary<(string, Guid, DateOnly), IReadOnlyList<QualificationGap>>(),
            QualificationInfo = new Dictionary<Guid, QualificationInfo>(),
            ShiftNames = new Dictionary<Guid, string>(),
        });

        var availability = Substitute.For<IAvailabilityIneligibilityService>();
        availability.GetAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlySet<(string, Guid, DateOnly)>>(new HashSet<(string, Guid, DateOnly)>()));

        var windows = Substitute.For<IWizardRestrictedWindowBuilder>();
        windows.BuildAsync(default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<CoreRestrictedTimeWindow>>([]));

        var keywords = Substitute.For<IScheduleCommandKeywordProvider>();
        keywords.GetAsync(default).ReturnsForAnyArgs(ScheduleCommandKeywordTestFactory.Default);

        var ruleSetLoader = Substitute.For<IPlanningRuleSetLoader>();
        ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], []));

        return new HarmonizerContextBuilder(_context, _contractProvider, softening, eligibility, availability, windows, keywords, ruleSetLoader);
    }
}
