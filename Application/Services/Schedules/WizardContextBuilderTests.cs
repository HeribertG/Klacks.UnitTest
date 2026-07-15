// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.ScheduleOptimizer.Models;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WizardContextBuilderTests
{
    private IClientContractDataProvider _contractProvider = null!;
    private IWizardShiftBuilder _shiftBuilder = null!;
    private IWizardHardConstraintBuilder _hardBuilder = null!;
    private IPeriodHoursService _periodHours = null!;
    private IAvailabilityIneligibilityService _availabilityService = null!;
    private WizardContextBuilder _sut = null!;

    [SetUp]
    public void Setup()
    {
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _shiftBuilder = Substitute.For<IWizardShiftBuilder>();
        _hardBuilder = Substitute.For<IWizardHardConstraintBuilder>();
        _periodHours = Substitute.For<IPeriodHoursService>();

        _periodHours
            .GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns(ci => (ci.Arg<DateOnly>(), ci.Arg<DateOnly>().AddMonths(1)));
        _periodHours
            .GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>());

        _contractProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData
            {
                MinPauseHours = 11,
                MaxDailyHours = 10,
                MaxConsecutiveDays = 6,
                MaxOptimalGap = 2,
                MaxWeeklyHours = 50,
            });

        var agentBuilder = new WizardAgentSnapshotBuilder(_contractProvider);
        var eligibilityBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        eligibilityBuilder
            .BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
                Arg.Any<CancellationToken>())
            .Returns(EligibilityMatrix.Empty);
        _availabilityService = Substitute.For<IAvailabilityIneligibilityService>();
        _availabilityService
            .GetAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<AvailabilityShiftSlot>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<(string, Guid, DateOnly)>)new HashSet<(string, Guid, DateOnly)>());
        _sut = new WizardContextBuilder(agentBuilder, _shiftBuilder, _hardBuilder, _periodHours, _contractProvider, eligibilityBuilder, _availabilityService);
    }

    [Test]
    public async Task BuildContextAsync_AvailabilityBlock_WithoutHigherLayer_IsKept()
    {
        var agentId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);
        ArrangeAvailabilityHierarchyCase(agentId, shiftId, date, new HardConstraintResult([], [], [], [], []));

        var result = await _sut.BuildContextAsync(HierarchyRequest(agentId, date), CancellationToken.None);

        result.IneligibleAssignments.ShouldContain((agentId.ToString(), shiftId, date));
    }

    [Test]
    public async Task BuildContextAsync_AvailabilityBlock_OnKeywordDay_IsSuppressed()
    {
        var agentId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);
        var keyword = new CoreScheduleCommand(agentId.ToString(), date, ScheduleCommandKeyword.OnlyLate);
        ArrangeAvailabilityHierarchyCase(agentId, shiftId, date, new HardConstraintResult([keyword], [], [], [], []));

        var result = await _sut.BuildContextAsync(HierarchyRequest(agentId, date), CancellationToken.None);

        result.IneligibleAssignments.ShouldNotContain((agentId.ToString(), shiftId, date));
    }

    [Test]
    public async Task BuildContextAsync_AvailabilityBlock_OnBreakDay_IsSuppressed()
    {
        var agentId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);
        var breakBlocker = new CoreBreakBlocker(agentId.ToString(), date, date, "Vacation");
        ArrangeAvailabilityHierarchyCase(agentId, shiftId, date, new HardConstraintResult([], [], [breakBlocker], [], []));

        var result = await _sut.BuildContextAsync(HierarchyRequest(agentId, date), CancellationToken.None);

        result.IneligibleAssignments.ShouldNotContain((agentId.ToString(), shiftId, date));
    }

    private void ArrangeAvailabilityHierarchyCase(
        Guid agentId, Guid shiftId, DateOnly date, HardConstraintResult hardConstraints)
    {
        SetupContractsWithGuaranteedHours(new Dictionary<Guid, decimal> { [agentId] = 30 });

        _hardBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(hardConstraints);

        _availabilityService
            .GetAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<AvailabilityShiftSlot>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<(string, Guid, DateOnly)>)new HashSet<(string, Guid, DateOnly)>
            {
                (agentId.ToString(), shiftId, date),
            });
    }

    private static WizardContextRequest HierarchyRequest(Guid agentId, DateOnly date)
        => new(
            PeriodFrom: date,
            PeriodUntil: date,
            AgentIds: new[] { agentId },
            ShiftIds: null,
            AnalyseToken: null);

    [Test]
    public async Task BuildContextAsync_ComposesAllSubBuildersAndPropagatesAnalyseToken()
    {
        var agentId = Guid.NewGuid();
        var scenarioToken = Guid.NewGuid();

        _contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>
            {
                [agentId] = new EffectiveContractData
                {
                    HasActiveContract = true,
                    ContractId = Guid.NewGuid(),
                    FullTime = 40,
                    GuaranteedHours = 30,
                    MaxDailyHours = 10,
                    MaxWeeklyHours = 50,
                    MinPauseHours = 11,
                    MaxConsecutiveDays = 6,
                },
            });

        _shiftBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CoreShift>());

        _hardBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new HardConstraintResult([], [], [], [], []));

        var request = new WizardContextRequest(
            PeriodFrom: new DateOnly(2026, 4, 20),
            PeriodUntil: new DateOnly(2026, 4, 22),
            AgentIds: new[] { agentId },
            ShiftIds: null,
            AnalyseToken: scenarioToken);

        var result = await _sut.BuildContextAsync(request, CancellationToken.None);

        result.PeriodFrom.ShouldBe(new DateOnly(2026, 4, 20));
        result.PeriodUntil.ShouldBe(new DateOnly(2026, 4, 22));
        result.AnalyseToken.ShouldBe(scenarioToken);
        result.Agents.Count().ShouldBe(1);
        result.ContractDays.Count().ShouldBe(3);
    }

    [Test]
    public async Task BuildContextAsync_DefaultOrder_ReshapesByGuaranteedHoursDescending()
    {
        var lowHoursFirst = Guid.NewGuid();
        var highHoursSecond = Guid.NewGuid();
        var midHoursThird = Guid.NewGuid();

        SetupContractsWithGuaranteedHours(new Dictionary<Guid, decimal>
        {
            [lowHoursFirst] = 10,
            [highHoursSecond] = 40,
            [midHoursThird] = 25,
        });

        var request = new WizardContextRequest(
            PeriodFrom: new DateOnly(2026, 4, 20),
            PeriodUntil: new DateOnly(2026, 4, 20),
            AgentIds: new[] { lowHoursFirst, highHoursSecond, midHoursThird },
            ShiftIds: null,
            AnalyseToken: null,
            AgentOrderIsUserDefined: false);

        var result = await _sut.BuildContextAsync(request, CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(
            new[] { highHoursSecond.ToString(), midHoursThird.ToString(), lowHoursFirst.ToString() });
    }

    [Test]
    public async Task BuildContextAsync_UserDefinedOrder_KeepsRequestOrderVerbatim()
    {
        var lowHoursFirst = Guid.NewGuid();
        var highHoursSecond = Guid.NewGuid();

        SetupContractsWithGuaranteedHours(new Dictionary<Guid, decimal>
        {
            [lowHoursFirst] = 10,
            [highHoursSecond] = 40,
        });

        var request = new WizardContextRequest(
            PeriodFrom: new DateOnly(2026, 4, 20),
            PeriodUntil: new DateOnly(2026, 4, 20),
            AgentIds: new[] { lowHoursFirst, highHoursSecond },
            ShiftIds: null,
            AnalyseToken: null,
            AgentOrderIsUserDefined: true);

        var result = await _sut.BuildContextAsync(request, CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(
            new[] { lowHoursFirst.ToString(), highHoursSecond.ToString() });
    }

    [Test]
    public async Task BuildContextAsync_EqualGuaranteedHours_ReshapeIsStable()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();

        SetupContractsWithGuaranteedHours(new Dictionary<Guid, decimal>
        {
            [firstId] = 20,
            [secondId] = 20,
            [thirdId] = 20,
        });

        var request = new WizardContextRequest(
            PeriodFrom: new DateOnly(2026, 4, 20),
            PeriodUntil: new DateOnly(2026, 4, 20),
            AgentIds: new[] { firstId, secondId, thirdId },
            ShiftIds: null,
            AnalyseToken: null,
            AgentOrderIsUserDefined: false);

        var result = await _sut.BuildContextAsync(request, CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(
            new[] { firstId.ToString(), secondId.ToString(), thirdId.ToString() });
    }

    private void SetupContractsWithGuaranteedHours(IReadOnlyDictionary<Guid, decimal> guaranteedHoursPerAgent)
    {
        var contracts = guaranteedHoursPerAgent.ToDictionary(
            kv => kv.Key,
            kv => new EffectiveContractData
            {
                HasActiveContract = true,
                ContractId = Guid.NewGuid(),
                FullTime = 40,
                GuaranteedHours = kv.Value,
                MaxDailyHours = 10,
                MaxWeeklyHours = 50,
                MinPauseHours = 11,
                MaxConsecutiveDays = 6,
            });

        _contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(contracts);

        _shiftBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CoreShift>());

        _hardBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new HardConstraintResult([], [], [], [], []));
    }

    [Test]
    public void BuildContextAsync_PropagatesCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns<Dictionary<Guid, EffectiveContractData>>(_ => throw new OperationCanceledException(cts.Token));

        var request = new WizardContextRequest(
            PeriodFrom: new DateOnly(2026, 4, 20),
            PeriodUntil: new DateOnly(2026, 4, 22),
            AgentIds: new[] { Guid.NewGuid() },
            ShiftIds: null,
            AnalyseToken: null);

        var act = async () => await _sut.BuildContextAsync(request, cts.Token);
        act.ShouldThrowAsync<OperationCanceledException>();
    }
}
