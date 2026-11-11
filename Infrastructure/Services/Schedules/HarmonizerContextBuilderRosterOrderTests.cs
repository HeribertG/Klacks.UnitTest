// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Wizard 2/3 take the row order from the one roster priority order of Wizard 1 (RosterPriorityOrder): a hand-arranged
/// order stays verbatim (stand-ins last), otherwise guaranteed hours, then full-time hours, descending, both from the
/// same contract data and membership windows as Wizard 1 and scaled to the planned period with member days only
/// (RosterRankHours), so weekly and monthly contracts compare fairly. The builder ranks once and tells the row sorter to keep it (KeepAgentOrder). A
/// missing weekly cap falls back to the Wizard 1 default instead of "unlimited". The daily cap is Wizard 1's agent cap
/// (planning contract basis, not the first period day; default when missing) plus the contract value of each day.
/// </summary>
[TestFixture]
public class HarmonizerContextBuilderRosterOrderTests
{
    private const decimal ContractDailyCap = 12m;
    private const decimal FirstDayOnlyDailyCap = 7m;

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
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task UserDefinedOrder_SetsKeepAgentOrder_AndRowOrderFollowsAgentIds()
    {
        var standIn = Guid.NewGuid();
        var low = Guid.NewGuid();
        var high = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [standIn] = Data(guaranteed: 200m, fullTime: 200m),
            [low] = Data(guaranteed: 10m, fullTime: 40m),
            [high] = Data(guaranteed: 160m, fullTime: 160m),
        });
        SetStandIn(standIn);

        var input = await BuildAsync([standIn, low, high], userDefined: true);
        var rows = RowSorter.Sort(BitmapBuilder.Build(input), input.KeepAgentOrder).Rows;

        input.KeepAgentOrder.ShouldBeTrue();
        input.Agents.Select(a => a.Id).ShouldBe([low.ToString(), high.ToString(), standIn.ToString()]);
        rows.Select(r => r.Id).ShouldBe([low.ToString(), high.ToString(), standIn.ToString()]);
    }

    [Test]
    public async Task RepeatedAgentId_BuildsOneRowAtTheFirstOccurrence_OthersKeepTheirOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [first] = Data(guaranteed: 10m, fullTime: 40m),
            [second] = Data(guaranteed: 20m, fullTime: 40m),
            [third] = Data(guaranteed: 30m, fullTime: 40m),
        });

        var input = await BuildAsync([second, first, second, third, first], userDefined: true);
        var rows = RowSorter.Sort(BitmapBuilder.Build(input), input.KeepAgentOrder).Rows;

        var expected = new[] { second, first, third }.Select(id => id.ToString());
        input.Agents.Select(a => a.Id).ShouldBe(expected);
        rows.Select(r => r.Id).ShouldBe(expected);
    }

    [Test]
    public async Task AutomaticOrder_RanksLikeWizard1_GuaranteedThenFullTime_Stable()
    {
        var noGuaranteeSmall = Guid.NewGuid();
        var guaranteed = Guid.NewGuid();
        var noGuaranteeLarge = Guid.NewGuid();
        var noGuaranteeLargeTwin = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [noGuaranteeSmall] = Data(guaranteed: 0m, fullTime: 10m),
            [guaranteed] = Data(guaranteed: 5m, fullTime: 0m),
            [noGuaranteeLarge] = Data(guaranteed: 0m, fullTime: 42m),
            [noGuaranteeLargeTwin] = Data(guaranteed: 0m, fullTime: 42m),
        });

        var input = await BuildAsync([noGuaranteeSmall, guaranteed, noGuaranteeLarge, noGuaranteeLargeTwin], userDefined: false);
        var rows = RowSorter.Sort(BitmapBuilder.Build(input), input.KeepAgentOrder).Rows;

        var expected = new[] { guaranteed, noGuaranteeLarge, noGuaranteeLargeTwin, noGuaranteeSmall }.Select(id => id.ToString());
        input.Agents.Select(a => a.Id).ShouldBe(expected);
        rows.Select(r => r.Id).ShouldBe(expected);
    }

    [Test]
    public async Task AutomaticOrder_Weekly40RanksAboveMonthly100_OnAOneWeekPlan()
    {
        var monthly = Guid.NewGuid();
        var weekly = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [monthly] = Data(guaranteed: 100m, fullTime: 100m),
            [weekly] = Data(guaranteed: 40m, fullTime: 40m, PaymentInterval.Weekly),
        });

        var input = await BuildAsync([monthly, weekly], userDefined: false);

        input.Agents.Select(a => a.Id).ShouldBe([weekly.ToString(), monthly.ToString()]);
    }

    [Test]
    public async Task AutomaticOrder_EntryInsideThePeriod_RanksByTheMemberDays()
    {
        var joiner = Guid.NewGuid();
        var fullMember = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [joiner] = Data(guaranteed: 160m, fullTime: 160m),
            [fullMember] = Data(guaranteed: 100m, fullTime: 100m),
        });
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = joiner,
            ValidFrom = WeekEnd.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });
        _context.SaveChanges();

        var input = await BuildAsync([joiner, fullMember], userDefined: false);

        input.Agents.Select(a => a.Id).ShouldBe([fullMember.ToString(), joiner.ToString()]);
    }

    [Test]
    public async Task MissingContractCap_FallsBackLikeWizard1()
    {
        var withoutCap = Guid.NewGuid();
        var withCap = Guid.NewGuid();
        var withoutFirstDayContract = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [withoutCap] = Data(guaranteed: 0m, fullTime: 0m),
            [withCap] = Data(guaranteed: 0m, fullTime: 0m) with { MaxWeeklyHours = 42m },
        });
        _context.Client.Add(new Client { Id = withoutFirstDayContract, Name = "Agent", FirstName = "NoContract" });
        _context.SaveChanges();

        var input = await BuildAsync([withoutCap, withCap, withoutFirstDayContract], userDefined: true);

        var caps = input.Agents.ToDictionary(a => a.Id, a => a.MaxWeeklyHours);
        caps[withoutCap.ToString()].ShouldBe((decimal)WizardSchedulingDefaults.MaxWeeklyHours);
        caps[withCap.ToString()].ShouldBe(42m);
        caps[withoutFirstDayContract.ToString()].ShouldBe((decimal)WizardSchedulingDefaults.MaxWeeklyHours);
    }

    [Test]
    public async Task MissingContractDailyCap_FallsBackLikeWizard1()
    {
        var withoutCap = Guid.NewGuid();
        var withCap = Guid.NewGuid();
        var withoutContractData = Guid.NewGuid();
        Stub(new Dictionary<Guid, EffectiveContractData>
        {
            [withoutCap] = Data(guaranteed: 0m, fullTime: 0m),
            [withCap] = Data(guaranteed: 0m, fullTime: 0m) with { MaxDailyHours = ContractDailyCap },
        });
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>())
            .Returns(new Dictionary<Guid, EffectiveContractData>
            {
                [withoutCap] = Data(guaranteed: 0m, fullTime: 0m) with { MaxDailyHours = FirstDayOnlyDailyCap },
                [withCap] = Data(guaranteed: 0m, fullTime: 0m) with { MaxDailyHours = FirstDayOnlyDailyCap },
            });
        _context.Client.Add(new Client { Id = withoutContractData, Name = "Agent", FirstName = "NoContract" });
        _context.SaveChanges();

        var input = await BuildAsync([withoutCap, withCap, withoutContractData], userDefined: true);

        var caps = input.Agents.ToDictionary(a => a.Id, a => a.MaxDailyHours);
        caps[withoutCap.ToString()].ShouldBe((decimal)WizardSchedulingDefaults.MaxDailyHours);
        caps[withCap.ToString()].ShouldBe(ContractDailyCap);
        caps[withoutContractData.ToString()].ShouldBe((decimal)WizardSchedulingDefaults.MaxDailyHours);
        var availability = input.Availability.ShouldNotBeNull();
        availability[(withCap.ToString(), WeekStart)].MaxDailyHours.ShouldBe(ContractDailyCap);
        availability[(withoutCap.ToString(), WeekStart)].MaxDailyHours.ShouldBe(0m);
        availability[(withoutContractData.ToString(), WeekStart)].MaxDailyHours.ShouldBe(0m);
    }

    private static EffectiveContractData Data(
        decimal guaranteed, decimal fullTime, PaymentInterval interval = PaymentInterval.Monthly) => new()
    {
        HasActiveContract = true,
        GuaranteedHours = guaranteed,
        FullTime = fullTime,
        PaymentInterval = (int)interval,
        MaxConsecutiveDays = 7,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private void Stub(Dictionary<Guid, EffectiveContractData> dataByAgent)
    {
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>())
            .Returns(dataByAgent);

        var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            range[date] = new Dictionary<Guid, EffectiveContractData>(dataByAgent);
        }
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(range);

        foreach (var agent in dataByAgent.Keys)
        {
            _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = agent.ToString() });
        }
        _context.SaveChanges();
    }

    private void SetStandIn(Guid agent)
    {
        _context.Client.Single(c => c.Id == agent).Type = EntityTypeEnum.StandIn;
        _context.SaveChanges();
    }

    private Task<BitmapInput> BuildAsync(IReadOnlyList<Guid> agentIds, bool userDefined)
        => BuildSut().BuildContextAsync(
            new HarmonizerContextRequest(WeekStart, WeekEnd, agentIds, AnalyseToken: null, AgentOrderIsUserDefined: userDefined),
            CancellationToken.None);

    private HarmonizerContextBuilder BuildSut()
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
