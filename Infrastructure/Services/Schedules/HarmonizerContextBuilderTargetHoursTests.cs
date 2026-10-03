// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Regression for the stage-3 root cause (2026-10-03): the bitmap target was the raw monthly GuaranteedHours
/// although the bitmap only covers the planned range, so on a 7-day plan every row sat ~80% under target, the
/// TargetHoursDeviation feature saturated at 1.0 and the redistribute generator never saw an over-target row.
/// </summary>
[TestFixture]
public class HarmonizerContextBuilderTargetHoursTests
{
    private const decimal MonthlyGuaranteedHours = 180m;
    private const decimal ShiftHours = 8m;
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
    public async Task BuildContextAsync_MonthlyContractOnSevenDayPlan_ProratesTargetToTheRange()
    {
        var agent = Guid.NewGuid();
        StubContract(PaymentInterval.Monthly, MonthlyGuaranteedHours, agent);

        var input = await BuildSut().BuildContextAsync(new HarmonizerContextRequest(WeekStart, WeekEnd, [agent], AnalyseToken: null), CancellationToken.None);

        input.Agents.Single().TargetHours.ShouldBe(MonthlyGuaranteedHours * 7m / 31m, 0.0000001m);
    }

    [Test]
    public async Task BuildContextAsync_WeeklyContractOnSevenDayPlan_KeepsTheWeeklyTarget()
    {
        var agent = Guid.NewGuid();
        StubContract(PaymentInterval.Weekly, 42m, agent);

        var input = await BuildSut().BuildContextAsync(new HarmonizerContextRequest(WeekStart, WeekEnd, [agent], AnalyseToken: null), CancellationToken.None);

        input.Agents.Single().TargetHours.ShouldBe(42m, 0.0000001m);
    }

    [Test]
    public async Task BuildContextAsync_RowNearProratedTarget_DoesNotSaturateTargetDeviation()
    {
        var agent = Guid.NewGuid();
        StubContract(PaymentInterval.Monthly, MonthlyGuaranteedHours, agent);
        await SeedWorksAsync(agent, workDays: 5);

        var input = await BuildSut().BuildContextAsync(new HarmonizerContextRequest(WeekStart, WeekEnd, [agent], AnalyseToken: null), CancellationToken.None);
        var bitmap = BitmapBuilder.Build(input);

        var deviation = RowFeatureExtractor.Extract(bitmap, 0).TargetHoursDeviation;

        // 40h worked against 180*7/31 = 40.65h -> |dev| ~1.6% * scale 3 ~ 0.05; the raw 180h target gave 1.0.
        deviation.ShouldBeLessThan(0.1);
    }

    [Test]
    public async Task BuildContextAsync_RowOverProratedTarget_GivesRedistributeCandidates()
    {
        var over = Guid.NewGuid();
        var under = Guid.NewGuid();
        StubContract(PaymentInterval.Monthly, MonthlyGuaranteedHours, over, under);
        await SeedWorksAsync(over, workDays: 7);
        await SeedWorksAsync(under, workDays: 2);

        var input = await BuildSut().BuildContextAsync(
            new HarmonizerContextRequest(WeekStart, WeekEnd, [over, under], AnalyseToken: null), CancellationToken.None);
        var bitmap = BitmapBuilder.Build(input);

        var candidates = new RedistributeLoadCandidateGenerator().Generate(bitmap).ToList();

        candidates.ShouldNotBeEmpty("56h worked is over the prorated 40.6h target, 16h is under it");
        candidates.ShouldAllBe(c => bitmap.Rows[c.RowA].Id == over.ToString() && bitmap.Rows[c.RowB].Id == under.ToString());
    }

    [Test]
    public void ComputePeriodTargetHours_ContractChangeInsideRange_UsesEachDaysContract()
    {
        var agent = Guid.NewGuid();
        var from = new DateOnly(2026, 1, 29);
        var until = new DateOnly(2026, 2, 4);
        var data = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            var hours = date.Month == 1 ? 184m : 160m;
            data[date] = new Dictionary<Guid, EffectiveContractData>
            {
                [agent] = new() { GuaranteedHours = hours, PaymentInterval = (int)PaymentInterval.MonthlyTargetHours },
            };
        }

        var result = HarmonizerContextBuilder.ComputePeriodTargetHours(
            [agent], from, until, data, new Dictionary<Guid, IReadOnlyCollection<Period>>());

        result[agent].ShouldBe((184m * 3m / 31m) + (160m * 4m / 28m), 0.0000001m);
    }

    [Test]
    public void ComputePeriodTargetHours_WeeklyContractWithInheritedMonthlyValue_ProratesByCalendarMonth()
    {
        var agent = Guid.NewGuid();
        var data = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            data[date] = new Dictionary<Guid, EffectiveContractData>
            {
                [agent] = new()
                {
                    GuaranteedHours = MonthlyGuaranteedHours,
                    PaymentInterval = (int)PaymentInterval.Weekly,
                    GuaranteedHoursBasisInterval = (int)PaymentInterval.MonthlyTargetHours,
                },
            };
        }

        var result = HarmonizerContextBuilder.ComputePeriodTargetHours(
            [agent], WeekStart, WeekEnd, data, new Dictionary<Guid, IReadOnlyCollection<Period>>());

        result[agent].ShouldBe(MonthlyGuaranteedHours * 7m / 31m, 0.0000001m);
    }

    [Test]
    public void ComputePeriodTargetHours_AgentWithoutData_IsZero()
    {
        var agent = Guid.NewGuid();
        var result = HarmonizerContextBuilder.ComputePeriodTargetHours(
            [agent], WeekStart, WeekEnd,
            new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>(),
            new Dictionary<Guid, IReadOnlyCollection<Period>>());

        result[agent].ShouldBe(0m);
    }

    private void StubContract(PaymentInterval interval, decimal guaranteedHours, params Guid[] agents)
    {
        var data = new EffectiveContractData
        {
            GuaranteedHours = guaranteedHours,
            PaymentInterval = (int)interval,
            MaxConsecutiveDays = 7,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };
        var firstDay = agents.ToDictionary(a => a, _ => data);
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>())
            .Returns(firstDay);

        var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            range[date] = agents.ToDictionary(a => a, _ => data);
        }
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(range);

        foreach (var agent in agents)
        {
            _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = agent.ToString()[..4] });
        }
        _context.SaveChanges();
    }

    private async Task SeedWorksAsync(Guid agent, int workDays)
    {
        for (var d = 0; d < workDays; d++)
        {
            _context.Work.Add(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = agent,
                ShiftId = Guid.NewGuid(),
                CurrentDate = WeekStart.AddDays(d),
                StartTime = new TimeOnly(6, 0),
                EndTime = new TimeOnly(14, 0),
                WorkTime = ShiftHours,
            });
        }
        await _context.SaveChangesAsync();
    }

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
        keywords.GetAsync(default).ReturnsForAnyArgs(new ScheduleCommandKeywordSet
        {
            FreeToken = "FREE",
            NegFreeToken = "-FREE",
            EarlyToken = "EARLY",
            NegEarlyToken = "-EARLY",
            LateToken = "LATE",
            NegLateToken = "-LATE",
            NightToken = "NIGHT",
            NegNightToken = "-NIGHT",
        });

        return new HarmonizerContextBuilder(_context, _contractProvider, softening, eligibility, availability, windows, keywords);
    }
}
