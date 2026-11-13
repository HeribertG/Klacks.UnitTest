// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Shouldly;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
    private IEligibilityMatrixBuilder _eligibilityBuilder = null!;
    private WizardContextBuilder _sut = null!;
    private IClientRepository _clientRepository = null!;
    private IMembershipWindowReader _membershipReader = null!;

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

        _membershipReader = Substitute.For<IMembershipWindowReader>();
        _membershipReader
            .GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, MembershipWindow>)new Dictionary<Guid, MembershipWindow>());
        var agentBuilder = new WizardAgentSnapshotBuilder(_contractProvider, _membershipReader);
        _eligibilityBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        _eligibilityBuilder
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
        var warmStartBuilder = Substitute.For<IWizardWarmStartBuilder>();
        warmStartBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreWarmStartAssignment>)new List<CoreWarmStartAssignment>());
        var restrictedWindowBuilder = Substitute.For<IWizardRestrictedWindowBuilder>();
        restrictedWindowBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CoreRestrictedTimeWindow>)new List<CoreRestrictedTimeWindow>());
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository
            .GetStandInIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _sut = new WizardContextBuilder(agentBuilder, _shiftBuilder, _hardBuilder, _periodHours, _contractProvider, _eligibilityBuilder, _availabilityService, warmStartBuilder, restrictedWindowBuilder, _clientRepository);
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

    [TestCase(ScheduleCommandKeyword.OnlyLate)]
    [TestCase(ScheduleCommandKeyword.NotFree)]
    [TestCase(ScheduleCommandKeyword.Free)]
    [TestCase(ScheduleCommandKeyword.NoNight)]
    public async Task BuildContextAsync_AvailabilityBlock_OnKeywordDay_StaysHard(ScheduleCommandKeyword keywordValue)
    {
        var agentId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);
        var keyword = new CoreScheduleCommand(agentId.ToString(), date, keywordValue);
        ArrangeAvailabilityHierarchyCase(agentId, shiftId, date, new HardConstraintResult([keyword], [], [], [], []));

        var result = await _sut.BuildContextAsync(HierarchyRequest(agentId, date), CancellationToken.None);

        result.IneligibleAssignments.ShouldContain((agentId.ToString(), shiftId, date));
    }

    [TestCase(KeywordMode.Hard)]
    [TestCase(KeywordMode.Soft)]
    public async Task BuildContextAsync_PassesTheRequestedKeywordModeToTheContext(KeywordMode mode)
    {
        var agentId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);
        ArrangeAvailabilityHierarchyCase(agentId, Guid.NewGuid(), date, new HardConstraintResult([], [], [], [], []));

        var result = await _sut.BuildContextAsync(
            HierarchyRequest(agentId, date) with { KeywordMode = mode }, CancellationToken.None);

        result.KeywordMode.ShouldBe(mode);
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
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<DateOnly?>())
            .Returns(hardConstraints);

        _availabilityService
            .GetAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<AvailabilityShiftSlot>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<(string, Guid, DateOnly)>)new HashSet<(string, Guid, DateOnly)>
            {
                (agentId.ToString(), shiftId, date),
            });
    }

    [Test]
    public async Task BuildContextAsync_PassesTheLevelShortfallOfTheEligibilityMatrixThrough()
    {
        // Arrange
        var agentId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);
        ArrangeAvailabilityHierarchyCase(agentId, shiftId, date, new HardConstraintResult([], [], [], [], []));
        var shortfall = new Dictionary<(string AgentId, Guid ShiftId, DateOnly Date), int> { [(agentId.ToString(), shiftId, date)] = 2 };
        _eligibilityBuilder
            .BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new EligibilityMatrix
            {
                Ineligible = new HashSet<(string, Guid, DateOnly)>(),
                Gaps = new Dictionary<(string, Guid, DateOnly), IReadOnlyList<Klacks.Api.Application.DTOs.Schedules.QualificationGap>>(),
                QualificationInfo = new Dictionary<Guid, QualificationInfo>(),
                ShiftNames = new Dictionary<Guid, string>(),
                LevelShortfall = shortfall,
            });

        // Act
        var result = await _sut.BuildContextAsync(HierarchyRequest(agentId, date), CancellationToken.None);

        // Assert
        result.LevelShortfallOf(agentId.ToString(), shiftId, date).ShouldBe(2);
    }

    [TestCase("th-TH")]
    [TestCase("fi-FI")]
    [TestCase("ar-SA")]
    [TestCase("de-CH")]
    [TestCase("en-US")]
    public async Task BuildContextAsync_SlotsWrittenByTheShiftBuilder_ReachTheAvailabilityCheck_InAnyProcessCulture(
        string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var agentId = Guid.NewGuid();
            var shiftId = Guid.NewGuid();
            var date = new DateOnly(2026, 4, 20);
            ArrangeAvailabilityHierarchyCase(agentId, shiftId, date, new HardConstraintResult([], [], [], [], []));

            var options = new DbContextOptionsBuilder<DataBaseContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            await using var dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
            dbContext.Shift.Add(new Shift
            {
                Id = shiftId,
                Name = "Early",
                Abbreviation = "E",
                FromDate = date,
                UntilDate = date,
                StartShift = new TimeOnly(6, 30),
                EndShift = new TimeOnly(14, 15),
                WorkTime = 8m,
                Quantity = 1,
                IsMonday = true,
            });
            await dbContext.SaveChangesAsync();
            var written = await new WizardShiftBuilder(dbContext)
                .BuildAsync(null, date, date, null, CancellationToken.None);
            _shiftBuilder
                .BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(written);

            await _sut.BuildContextAsync(HierarchyRequest(agentId, date), CancellationToken.None);

            await _availabilityService.Received().GetAsync(
                Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Is<IReadOnlyList<AvailabilityShiftSlot>>(slots =>
                    slots.Count == 1
                    && slots[0].ShiftId == shiftId
                    && slots[0].Date == date
                    && slots[0].Start == new TimeOnly(6, 30)
                    && slots[0].End == new TimeOnly(14, 15)),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
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

        StubContractData(new Dictionary<Guid, EffectiveContractData>
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
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<DateOnly?>())
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
    public async Task BuildContextAsync_RepeatedAgentId_PlansTheAgentOnce_AtTheFirstOccurrence()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        SetupContractsWithGuaranteedHours(new Dictionary<Guid, decimal>
        {
            [first] = 10,
            [second] = 40,
        });

        var result = await _sut.BuildContextAsync(
            new WizardContextRequest(
                new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 20),
                new[] { second, first, second, first }, null, null, AgentOrderIsUserDefined: true),
            CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(new[] { second.ToString(), first.ToString() });
        result.ContractDays.Count().ShouldBe(2);
        await _hardBuilder.Received().BuildAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { second, first })),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<DateOnly?>());
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

    [Test]
    public async Task BuildContextAsync_UserDefinedOrder_StandInFirst_EndsLast_RealOrderKept()
    {
        var standIn = Guid.NewGuid();
        var lowHoursPerson = Guid.NewGuid();
        var highHoursPerson = Guid.NewGuid();
        SetupContractsWithGuaranteedHours(new Dictionary<Guid, decimal>
        {
            [standIn] = 0,
            [lowHoursPerson] = 10,
            [highHoursPerson] = 40,
        });
        var agentIds = new[] { standIn, lowHoursPerson, highHoursPerson };
        _clientRepository.GetStandInIdsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(agentIds)), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid> { standIn });

        var result = await _sut.BuildContextAsync(
            new WizardContextRequest(new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 20), agentIds, null, null, AgentOrderIsUserDefined: true),
            CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(
            new[] { lowHoursPerson.ToString(), highHoursPerson.ToString(), standIn.ToString() });
    }

    [Test]
    public async Task BuildContextAsync_HoursOrder_StandInsAfterEveryPerson_AlsoAfterOneWithoutHours()
    {
        var standInWithHours = Guid.NewGuid();
        var standIn = Guid.NewGuid();
        var personWithoutHours = Guid.NewGuid();
        var personWithHours = Guid.NewGuid();
        SetupContractsWithGuaranteedHours(new Dictionary<Guid, decimal>
        {
            [standInWithHours] = 50,
            [standIn] = 0,
            [personWithoutHours] = 0,
            [personWithHours] = 20,
        });
        var agentIds = new[] { standInWithHours, standIn, personWithoutHours, personWithHours };
        _clientRepository.GetStandInIdsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(agentIds)), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { standInWithHours, standIn });

        var result = await _sut.BuildContextAsync(
            new WizardContextRequest(new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 20), agentIds, null, null),
            CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(new[]
        {
            personWithHours.ToString(), personWithoutHours.ToString(), standInWithHours.ToString(), standIn.ToString(),
        });
        result.Agents.Select(a => a.IsStandIn).ShouldBe(new[] { false, false, true, true });
    }

    [Test]
    public async Task AutoOrder_NoGuarantee_SortsByFullTimeDesc_Stable()
    {
        var smallFirst = Guid.NewGuid();
        var largeSecond = Guid.NewGuid();
        var midThird = Guid.NewGuid();
        var midFourth = Guid.NewGuid();
        var guaranteedFifth = Guid.NewGuid();
        SetupContracts(new Dictionary<Guid, (decimal GuaranteedHours, decimal FullTime)>
        {
            [smallFirst] = (0, 10),
            [largeSecond] = (0, 42),
            [midThird] = (0, 20),
            [midFourth] = (0, 20),
            [guaranteedFifth] = (5, 0),
        });

        var result = await _sut.BuildContextAsync(
            new WizardContextRequest(
                new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 20),
                new[] { smallFirst, largeSecond, midThird, midFourth, guaranteedFifth }, null, null),
            CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(new[]
        {
            guaranteedFifth.ToString(), largeSecond.ToString(), midThird.ToString(), midFourth.ToString(), smallFirst.ToString(),
        });
    }

    private void SetupContractsWithGuaranteedHours(IReadOnlyDictionary<Guid, decimal> guaranteedHoursPerAgent)
        => SetupContracts(guaranteedHoursPerAgent.ToDictionary(kv => kv.Key, kv => (kv.Value, 40m)));

    [Test]
    public async Task AutoOrder_Weekly40RanksAboveMonthly100_OnAOneWeekPlan_TargetsStayRaw()
    {
        var monthly = Guid.NewGuid();
        var weekly = Guid.NewGuid();
        SetupContractData(new Dictionary<Guid, EffectiveContractData>
        {
            [monthly] = IntervalData(PaymentInterval.Monthly, guaranteedHours: 100m),
            [weekly] = IntervalData(PaymentInterval.Weekly, guaranteedHours: 40m),
        });

        var result = await _sut.BuildContextAsync(
            new WizardContextRequest(new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 26), new[] { monthly, weekly }, null, null),
            CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(new[] { weekly.ToString(), monthly.ToString() });
        result.Agents.Single(a => a.Id == weekly.ToString()).GuaranteedHours.ShouldBe(40.0);
        result.Agents.Single(a => a.Id == monthly.ToString()).GuaranteedHours.ShouldBe(100.0);
    }

    [Test]
    public async Task AutoOrder_EntryInsideThePeriod_RanksByTheMemberDays()
    {
        var joiner = Guid.NewGuid();
        var fullMember = Guid.NewGuid();
        var periodFrom = new DateOnly(2026, 4, 20);
        var periodUntil = new DateOnly(2026, 4, 26);
        SetupContractData(new Dictionary<Guid, EffectiveContractData>
        {
            [joiner] = IntervalData(PaymentInterval.Monthly, guaranteedHours: 160m),
            [fullMember] = IntervalData(PaymentInterval.Monthly, guaranteedHours: 100m),
        });
        _membershipReader
            .GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, MembershipWindow>)new Dictionary<Guid, MembershipWindow>
            {
                [joiner] = new(periodUntil.AddDays(-2), null),
            });

        var result = await _sut.BuildContextAsync(
            new WizardContextRequest(periodFrom, periodUntil, new[] { joiner, fullMember }, null, null),
            CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(new[] { fullMember.ToString(), joiner.ToString() });
    }

    private static EffectiveContractData IntervalData(PaymentInterval interval, decimal guaranteedHours) => new()
    {
        HasActiveContract = true,
        ContractId = Guid.NewGuid(),
        PaymentInterval = (int)interval,
        FullTime = guaranteedHours,
        GuaranteedHours = guaranteedHours,
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

    private void SetupContracts(IReadOnlyDictionary<Guid, (decimal GuaranteedHours, decimal FullTime)> hoursPerAgent)
    {
        var contracts = hoursPerAgent.ToDictionary(
            kv => kv.Key,
            kv => new EffectiveContractData
            {
                HasActiveContract = true,
                ContractId = Guid.NewGuid(),
                FullTime = kv.Value.FullTime,
                GuaranteedHours = kv.Value.GuaranteedHours,
                MaxDailyHours = 10,
                MaxWeeklyHours = 50,
                MinPauseHours = 11,
                MaxConsecutiveDays = 6,
            });

        SetupContractData(contracts);
    }

    private void SetupContractData(Dictionary<Guid, EffectiveContractData> contracts)
    {
        StubContractData(contracts);

        _shiftBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CoreShift>());

        _hardBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<DateOnly?>())
            .Returns(new HardConstraintResult([], [], [], [], []));
    }

    [Test]
    public void BuildContextAsync_PropagatesCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns<Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>>(
                _ => throw new OperationCanceledException(cts.Token));

        var request = new WizardContextRequest(
            PeriodFrom: new DateOnly(2026, 4, 20),
            PeriodUntil: new DateOnly(2026, 4, 22),
            AgentIds: new[] { Guid.NewGuid() },
            ShiftIds: null,
            AnalyseToken: null);

        var act = async () => await _sut.BuildContextAsync(request, cts.Token);
        act.ShouldThrowAsync<OperationCanceledException>();
    }
    /// <summary>
    /// Stubs both provider entry points with the same data: the agent snapshot builder resolves the
    /// whole period through the range API, the context builder still asks per day for its defaults.
    /// </summary>
    private void StubContractData(Dictionary<Guid, EffectiveContractData> contracts)
    {
        _contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(contracts);

        _contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                var from = ci.ArgAt<DateOnly>(1);
                var until = ci.ArgAt<DateOnly>(2);
                var result = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
                for (var date = from; date <= until; date = date.AddDays(1))
                {
                    result[date] = contracts;
                }

                return result;
            });
    }

    [Test]
    public async Task BuildContextAsync_ReplanFromInsideThePeriod_ReachesTheHardConstraintBuilder()
    {
        var agentId = Guid.NewGuid();
        var from = new DateOnly(2026, 4, 20);
        var until = new DateOnly(2026, 4, 24);
        var cut = new DateOnly(2026, 4, 22);
        ArrangeAvailabilityHierarchyCase(agentId, Guid.NewGuid(), from, new HardConstraintResult([], [], [], [], []));

        await _sut.BuildContextAsync(ReplanRequest(agentId, from, until, cut), CancellationToken.None);

        await _hardBuilder.Received(1).BuildAsync(
            Arg.Any<IReadOnlyList<Guid>>(), from, until, Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>(), cut);
    }

    [Test]
    public async Task BuildContextAsync_ReplanFromAtPeriodStart_IsNormalisedToNull()
    {
        var agentId = Guid.NewGuid();
        var from = new DateOnly(2026, 4, 20);
        var until = new DateOnly(2026, 4, 24);
        ArrangeAvailabilityHierarchyCase(agentId, Guid.NewGuid(), from, new HardConstraintResult([], [], [], [], []));

        await _sut.BuildContextAsync(ReplanRequest(agentId, from, until, from), CancellationToken.None);

        await _hardBuilder.Received(1).BuildAsync(
            Arg.Any<IReadOnlyList<Guid>>(), from, until, Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>(), null);
    }

    [Test]
    public async Task BuildContextAsync_ReplanFromAfterPeriodUntil_IsNormalisedToNull()
    {
        var agentId = Guid.NewGuid();
        var from = new DateOnly(2026, 4, 20);
        var until = new DateOnly(2026, 4, 24);
        ArrangeAvailabilityHierarchyCase(agentId, Guid.NewGuid(), from, new HardConstraintResult([], [], [], [], []));

        await _sut.BuildContextAsync(ReplanRequest(agentId, from, until, until.AddDays(1)), CancellationToken.None);

        await _hardBuilder.Received(1).BuildAsync(
            Arg.Any<IReadOnlyList<Guid>>(), from, until, Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>(), null);
    }

    [Test]
    public async Task BuildContextAsync_SeatHeldByAFixedWorkOutsideTheRun_IsNotOffered_EverythingDerivedSeesTheRest()
    {
        var agentId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);
        ArrangeAvailabilityHierarchyCase(agentId, shiftId, date, new HardConstraintResult([], [], [], [], []));
        var seat = new CoreShift(shiftId.ToString(), "Early", "2026-04-20", "06:00", "14:00", 8, 1, 0);
        _shiftBuilder
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CoreShift> { seat, seat });
        _hardBuilder
            .LoadForeignFixedSeatsAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, DateOnly)> { (shiftId, date) });

        var result = await _sut.BuildContextAsync(HierarchyRequest(agentId, date) with { AnalyseToken = token }, CancellationToken.None);

        result.Shifts.Count.ShouldBe(1);
        await _hardBuilder.Received(1).LoadForeignFixedSeatsAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { agentId })),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { shiftId })),
            date, date, token, Arg.Any<CancellationToken>());
        await _availabilityService.Received().GetAsync(
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Is<IReadOnlyList<AvailabilityShiftSlot>>(slots => slots.Count == 1),
            Arg.Any<CancellationToken>());
    }

    private static WizardContextRequest ReplanRequest(
        Guid agentId, DateOnly from, DateOnly until, DateOnly replanFrom)
        => new(
            PeriodFrom: from,
            PeriodUntil: until,
            AgentIds: new[] { agentId },
            ShiftIds: null,
            AnalyseToken: null,
            ReplanFrom: replanFrom);
}
