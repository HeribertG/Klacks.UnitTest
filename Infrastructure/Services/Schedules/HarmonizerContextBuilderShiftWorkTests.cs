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
/// Wizard 2 reads the shift-work flag with the same choice as Wizard 1 (PlanningContractBasis): first member day with
/// an active contract, else first member day with the settings defaults, from the per-day contract data - not from the
/// first day of the period. The first-day stub always carries the opposite flag so a wrong source fails the test.
/// </summary>
[TestFixture]
public class HarmonizerContextBuilderShiftWorkTests
{
    private const int JoinOffsetDays = 3;
    private static readonly DateOnly WeekStart = new(2026, 1, 5);
    private static readonly DateOnly WeekEnd = new(2026, 1, 11);

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractProvider = null!;
    private IPlanningRuleSetLoader _ruleSetLoader = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _ruleSetLoader = Substitute.For<IPlanningRuleSetLoader>();
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], []));
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(true)]
    [TestCase(false)]
    public async Task ActiveContract_FlagComesFromThePerDayContractData(bool performsShiftWork)
    {
        var agent = Guid.NewGuid();
        Stub(agent, firstDayShiftWork: !performsShiftWork, _ => Data(active: true, shiftWork: performsShiftWork));

        var input = await BuildAsync(agent);

        input.Agents.Single().PerformsShiftWork.ShouldBe(performsShiftWork);
    }

    [Test]
    public async Task WithoutContract_TheSettingsDefaultsDecide()
    {
        var agent = Guid.NewGuid();
        Stub(agent, firstDayShiftWork: false, _ => Data(active: false, shiftWork: true));

        var input = await BuildAsync(agent);

        input.Agents.Single().PerformsShiftWork.ShouldBeTrue();
    }

    [Test]
    public async Task ContractStartingMidPeriod_WinsOverTheEarlierDefaults()
    {
        var agent = Guid.NewGuid();
        Stub(agent, firstDayShiftWork: false, date => date < WeekStart.AddDays(JoinOffsetDays)
            ? Data(active: false, shiftWork: false)
            : Data(active: true, shiftWork: true));

        var input = await BuildAsync(agent);

        input.Agents.Single().PerformsShiftWork.ShouldBeTrue();
    }

    [Test]
    public async Task MidPeriodJoiner_TheFirstMemberDayDecides()
    {
        var agent = Guid.NewGuid();
        Stub(agent, firstDayShiftWork: false, date => Data(active: true, shiftWork: date >= WeekStart.AddDays(JoinOffsetDays)));
        SeedMembership(agent, WeekStart.AddDays(JoinOffsetDays), validUntil: null);

        var input = await BuildAsync(agent);

        input.Agents.Single().PerformsShiftWork.ShouldBeTrue();
    }

    [Test]
    public async Task NoMemberDayInThePeriod_NoShiftWork()
    {
        var agent = Guid.NewGuid();
        Stub(agent, firstDayShiftWork: true, _ => Data(active: true, shiftWork: true));
        SeedMembership(agent, WeekStart.AddDays(-JoinOffsetDays * 2), WeekStart.AddDays(-1));

        var input = await BuildAsync(agent);

        input.Agents.Single().PerformsShiftWork.ShouldBeFalse();
    }

    [Test]
    public async Task PlanningRuleAgents_KeepTheFlag()
    {
        var agent = Guid.NewGuid();
        Stub(agent, firstDayShiftWork: false, _ => Data(active: true, shiftWork: true));
        var rule = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 3);
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([rule], [new RuleAgent(agent.ToString(), null, 80m)], [], [], []));

        var input = await BuildAsync(agent);

        input.Agents.Single().WorkloadPercent.ShouldBe(80m);
        input.Agents.Single().PerformsShiftWork.ShouldBeTrue();
    }

    private static EffectiveContractData Data(bool active, bool shiftWork) => new()
    {
        HasActiveContract = active,
        PerformsShiftWork = shiftWork,
        GuaranteedHours = 160m,
        PaymentInterval = (int)PaymentInterval.Monthly,
        MaxConsecutiveDays = 7,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private void Stub(Guid agent, bool firstDayShiftWork, Func<DateOnly, EffectiveContractData> dataOn)
    {
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>())
            .Returns(new Dictionary<Guid, EffectiveContractData> { [agent] = Data(active: true, shiftWork: firstDayShiftWork) });

        var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            range[date] = new Dictionary<Guid, EffectiveContractData> { [agent] = dataOn(date) };
        }
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(range);

        _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = "ShiftWork" });
        _context.SaveChanges();
    }

    private void SeedMembership(Guid agent, DateOnly validFrom, DateOnly? validUntil)
    {
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            ValidFrom = validFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ValidUntil = validUntil?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });
        _context.SaveChanges();
    }

    private Task<BitmapInput> BuildAsync(Guid agent)
        => BuildSut().BuildContextAsync(new HarmonizerContextRequest(WeekStart, WeekEnd, [agent], AnalyseToken: null), CancellationToken.None);

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

        return new HarmonizerContextBuilder(_context, _contractProvider, softening, eligibility, availability, windows, keywords, _ruleSetLoader);
    }
}
