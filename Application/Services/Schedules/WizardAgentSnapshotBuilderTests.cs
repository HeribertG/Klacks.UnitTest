// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.ScheduleOptimizer.Models;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WizardAgentSnapshotBuilderTests
{
    private IClientContractDataProvider _contractProvider = null!;
    private IMembershipWindowReader _membershipReader = null!;
    private WizardAgentSnapshotBuilder _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _membershipReader = Substitute.For<IMembershipWindowReader>();
        StubMembership(new Dictionary<Guid, MembershipWindow>());
        _sut = new WizardAgentSnapshotBuilder(_contractProvider, _membershipReader);
    }

    [Test]
    public async Task BuildAsync_MembershipEndingMidPeriod_ClosesEveryDayAfterTheExit()
    {
        var agentId = Guid.NewGuid();
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract() });
        StubMembership(new Dictionary<Guid, MembershipWindow>
        {
            [agentId] = new(new DateOnly(2020, 1, 1), new DateOnly(2026, 3, 15)),
        });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 3, 13), new DateOnly(2026, 3, 17), new DateOnly(2026, 3, 13), new DateOnly(2026, 3, 17),
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.ContractDays.Where(d => d.WorksOnDay).Select(d => d.Date).ShouldBe(
            [new DateOnly(2026, 3, 13), new DateOnly(2026, 3, 14), new DateOnly(2026, 3, 15)],
            "The exit day itself is still a member day (inclusive), the days after it are closed.");
        result.ContractDays.Count.ShouldBe(5);
    }

    [Test]
    public async Task BuildAsync_MembershipStartingMidPeriod_ClosesTheDaysBeforeTheEntryAndTakesTheMasterDataFromTheEntryDay()
    {
        var agentId = Guid.NewGuid();
        var entry = new DateOnly(2026, 3, 10);
        StubContractData(date => new Dictionary<Guid, EffectiveContractData>
        {
            [agentId] = AllWeekContract(guaranteedHours: date < entry ? 10 : 120),
        });
        StubMembership(new Dictionary<Guid, MembershipWindow> { [agentId] = new(entry, null) });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 12),
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.ContractDays.Where(d => !d.WorksOnDay).Select(d => d.Date).ShouldBe(
            [new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 9)]);
        result.Agents.Single().GuaranteedHours.ShouldBe(
            120.0 * 3 / 5,
            "Master data comes from the first member day with a contract (120, not 10), prorated to the 3 member days of the 5-day period.");
    }

    [Test]
    public async Task BuildAsync_WithoutAnyContractAndEntryMidPeriod_ClosesTheDaysBeforeTheEntryAndUsesTheDefaultsOfTheEntryDay()
    {
        var agentId = Guid.NewGuid();
        var entry = new DateOnly(2026, 3, 10);
        StubContractData(date => new Dictionary<Guid, EffectiveContractData>
        {
            [agentId] = AllWeekDefaults(guaranteedHours: date < entry ? 10 : 100),
        });
        StubMembership(new Dictionary<Guid, MembershipWindow> { [agentId] = new(entry, null) });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 12),
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.ContractDays.Where(d => !d.WorksOnDay).Select(d => d.Date).ShouldBe(
            [new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 9)],
            "Without any contract only the days before the entry are closed; the member days follow the settings defaults.");
        result.Agents.Single().GuaranteedHours.ShouldBe(
            100.0 * 3 / 5,
            "Master data comes from the defaults of the first member day (100, not 10), prorated to the 3 member days of the 5-day period.");
    }

    [Test]
    public async Task BuildAsync_OutsideTheMembershipWithoutContractData_StillEmitsAClosedDay()
    {
        var agentId = Guid.NewGuid();
        var lastMemberDay = new DateOnly(2026, 3, 15);
        StubContractData(date => date <= lastMemberDay
            ? new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract() }
            : new Dictionary<Guid, EffectiveContractData>());
        StubMembership(new Dictionary<Guid, MembershipWindow> { [agentId] = new(new DateOnly(2020, 1, 1), lastMemberDay) });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 16),
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.ContractDays.Single(d => d.Date == new DateOnly(2026, 3, 16)).WorksOnDay.ShouldBeFalse(
            "Without a contract day the engine would fall back to the weekday flags and plan the agent after the exit.");
    }

    [Test]
    public async Task BuildAsync_MembershipEndedBeforeThePeriod_ExcludesTheAgent()
    {
        var agentId = Guid.NewGuid();
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract() });
        StubMembership(new Dictionary<Guid, MembershipWindow>
        {
            [agentId] = new(new DateOnly(2020, 1, 1), new DateOnly(2026, 2, 28)),
        });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 3), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 3),
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.Agents.ShouldBeEmpty();
        result.ContractDays.ShouldAllBe(d => !d.WorksOnDay);
    }

    private const double Tolerance = 1e-9;
    private static readonly DateOnly MarchFirst = new(2026, 3, 1);
    private static readonly DateOnly MarchLast = new(2026, 3, 31);

    private async Task<CoreAgent> MarchAgentAsync(MembershipWindow? window)
    {
        var agentId = Guid.NewGuid();
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData>
        {
            [agentId] = AllWeekContract(guaranteedHours: 124) with { FullTime = 155, MinimumHours = 62, MaximumHours = 186 },
        });
        StubMembership(window is null
            ? new Dictionary<Guid, MembershipWindow>()
            : new Dictionary<Guid, MembershipWindow> { [agentId] = window });

        var result = await _sut.BuildAsync(
            new[] { agentId }, MarchFirst, MarchLast, MarchFirst, MarchLast, new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);
        return result.Agents.Single();
    }

    [Test]
    public async Task BuildAsync_ExitOnThe15th_ProratesTheTargetsTo15Of31_AndKeepsTheMaximum()
    {
        var agent = await MarchAgentAsync(new MembershipWindow(new DateOnly(2020, 1, 1), new DateOnly(2026, 3, 15)));

        agent.GuaranteedHours.ShouldBe((double)(124m * 15 / 31), Tolerance);
        agent.FullTime.ShouldBe((double)(155m * 15 / 31), Tolerance);
        agent.MinimumHours.ShouldBe((double)(62m * 15 / 31), Tolerance);
        agent.MaximumHours.ShouldBe(186, "MaximumHours is a hard ceiling and is never prorated");
    }

    [Test]
    public async Task BuildAsync_EntryOnThe10th_ProratesTheTargetsTo22Of31()
    {
        var agent = await MarchAgentAsync(new MembershipWindow(new DateOnly(2026, 3, 10), null));

        agent.GuaranteedHours.ShouldBe((double)(124m * 22 / 31), Tolerance);
        agent.FullTime.ShouldBe((double)(155m * 22 / 31), Tolerance);
        agent.MaximumHours.ShouldBe(186);
    }

    [Test]
    public async Task BuildAsync_LastWeekOfTheMonthWithExitOnThe27th_ProratesAgainstThePayPeriodNotThePlannedRange()
    {
        var agentId = Guid.NewGuid();
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract(guaranteedHours: 124) });
        StubMembership(new Dictionary<Guid, MembershipWindow>
        {
            [agentId] = new(new DateOnly(2020, 1, 1), new DateOnly(2026, 3, 27)),
        });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 3, 25), MarchLast, MarchFirst, MarchLast,
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.Agents.Single().GuaranteedHours.ShouldBe((double)(124m * 27 / 31), Tolerance,
            "CurrentHours carries 01.-24.03., so the target stays a pay-period target (27/31), not 3/7 of it.");
    }

    [Test]
    public async Task BuildAsync_EntryBeforeAPartialPlannedRange_StillProratesAgainstThePayPeriod()
    {
        var agentId = Guid.NewGuid();
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract(guaranteedHours: 124) });
        StubMembership(new Dictionary<Guid, MembershipWindow> { [agentId] = new(new DateOnly(2026, 3, 10), null) });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 3, 15), MarchLast, MarchFirst, MarchLast,
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.Agents.Single().GuaranteedHours.ShouldBe((double)(124m * 22 / 31), Tolerance);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BuildAsync_WithoutAMembershipBoundaryInThePeriod_KeepsTheTargetsUnchanged(bool withMembershipRow)
    {
        var agent = await MarchAgentAsync(withMembershipRow ? new MembershipWindow(new DateOnly(2020, 1, 1), new DateOnly(2027, 1, 1)) : null);

        agent.GuaranteedHours.ShouldBe(124);
        agent.FullTime.ShouldBe(155);
        agent.MinimumHours.ShouldBe(62);
        agent.MaximumHours.ShouldBe(186);
    }

    [Test]
    public async Task BuildAsync_WithAGroup_ClosesTheDaysOutsideTheGroupPeriodOfAnOpenMember()
    {
        var agentId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var fairFrom = new DateOnly(2026, 12, 10);
        var fairUntil = new DateOnly(2026, 12, 13);
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract() });
        StubMembership(new Dictionary<Guid, MembershipWindow> { [agentId] = new(new DateOnly(2026, 11, 1), null) });
        StubGroupPeriod(agentId, groupId, fairFrom, fairUntil);

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 15), new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 15),
            new Dictionary<Guid, double>(), groupId, CancellationToken.None);

        result.ContractDays.Where(d => d.WorksOnDay).Select(d => d.Date).ShouldBe(
            [fairFrom, new DateOnly(2026, 12, 11), new DateOnly(2026, 12, 12), fairUntil],
            "A temporary worker whose group membership covers the fair days only must not be planned on the other days, "
            + "although the company membership is open.");
        result.ContractDays.Count.ShouldBe(8);
    }

    [Test]
    public async Task BuildAsync_WithAGroup_ClosesADayOutsideTheGroupPeriodEvenWithoutContractData()
    {
        var agentId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var lastGroupDay = new DateOnly(2026, 12, 13);
        StubContractData(date => date <= lastGroupDay
            ? new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract() }
            : new Dictionary<Guid, EffectiveContractData>());
        StubGroupPeriod(agentId, groupId, new DateOnly(2026, 12, 10), lastGroupDay);

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 12, 13), new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 13), new DateOnly(2026, 12, 14),
            new Dictionary<Guid, double>(), groupId, CancellationToken.None);

        result.ContractDays.Single(d => d.Date == new DateOnly(2026, 12, 14)).WorksOnDay.ShouldBeFalse(
            "Without a contract day the engine would fall back to the weekday flags and plan the agent after the group period.");
    }

    [Test]
    public async Task BuildAsync_WithAGroup_KeepsTheTargetProratedByTheCompanyMembershipOnly()
    {
        var agentId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract(guaranteedHours: 124) });
        StubGroupPeriod(agentId, groupId, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 13));

        var result = await _sut.BuildAsync(
            new[] { agentId }, MarchFirst, MarchLast, MarchFirst, MarchLast,
            new Dictionary<Guid, double>(), groupId, CancellationToken.None);

        result.Agents.Single().GuaranteedHours.ShouldBe(
            124, "The group period only closes days; prorating the target by group days would be a separate owner decision.");
    }

    [Test]
    public async Task BuildAsync_WithoutAGroup_DoesNotReadAnyGroupPeriod()
    {
        var agentId = Guid.NewGuid();
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = AllWeekContract() });

        var result = await _sut.BuildAsync(
            new[] { agentId }, new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 9), new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 9),
            new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        result.ContractDays.ShouldAllBe(d => d.WorksOnDay);
        await _membershipReader.DidNotReceiveWithAnyArgs().GetGroupPeriodRangesAsync(default!, default, default);
    }

    private void StubGroupPeriod(Guid agentId, Guid groupId, DateOnly from, DateOnly until)
    {
        _membershipReader
            .GetGroupPeriodRangesAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(agentId)), groupId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (DateTime? ValidFrom, DateTime? ValidUntil)>
            {
                [agentId] = (from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), until.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            });
    }

    private void StubMembership(Dictionary<Guid, MembershipWindow> windows)
    {
        _membershipReader
            .GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, MembershipWindow>)windows);
    }

    private static EffectiveContractData AllWeekDefaults(decimal guaranteedHours) =>
        AllWeekContract(guaranteedHours, hasActiveContract: false);

    private static EffectiveContractData AllWeekContract(decimal guaranteedHours = 120, bool hasActiveContract = true) => new()
    {
        HasActiveContract = hasActiveContract,
        ContractId = hasActiveContract ? Guid.NewGuid() : null,
        FullTime = 160,
        GuaranteedHours = guaranteedHours,
        MaxDailyHours = 10,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
        PerformsShiftWork = true,
    };

    [Test]
    public async Task BuildAsync_ReturnsOneAgentAndOneContractDayPerDate()
    {
        var agentId = Guid.NewGuid();
        var from = new DateOnly(2026, 4, 20);
        var until = new DateOnly(2026, 4, 22);

        var contractData = new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            FullTime = 40,
            GuaranteedHours = 30,
            MaxDailyHours = 10,
            MaxWeeklyHours = 50,
            MinPauseHours = 11,
            MaxOptimalGap = 2,
            MaxConsecutiveDays = 6,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnSaturday = false,
            PerformsShiftWork = true,
        };

        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = contractData });

        var result = await _sut.BuildAsync(
            new[] { agentId }, from, until, from, until,
            new Dictionary<Guid, double>(),
            groupId: null, CancellationToken.None);

        result.Agents.Count().ShouldBe(1);
        result.ContractDays.Count().ShouldBe(3);
        result.ContractDays.ShouldAllBe(d => d.AgentId == agentId.ToString());
    }

    [Test]
    public async Task BuildAsync_MapsContractFlagsToCoreAgent()
    {
        var agentId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);

        var contractData = new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            FullTime = 42,
            GuaranteedHours = 30,
            MaximumHours = 45,
            MinimumHours = 25,
            MaxDailyHours = 10,
            MaxWeeklyHours = 50,
            MinPauseHours = 11,
            MaxOptimalGap = 2,
            MaxConsecutiveDays = 6,
            WorkOnMonday = true,
            WorkOnSaturday = true,
            PerformsShiftWork = false,
        };

        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = contractData });

        var result = await _sut.BuildAsync(
            new[] { agentId }, date, date, date, date,
            new Dictionary<Guid, double> { [agentId] = 12.5 },
            groupId: null, CancellationToken.None);

        var agent = result.Agents.Single();
        agent.CurrentHours.ShouldBe(12.5);
        agent.FullTime.ShouldBe(42);
        agent.MaximumHours.ShouldBe(45);
        agent.MinimumHours.ShouldBe(25);
        agent.PerformsShiftWork.ShouldBeFalse();
        agent.WorkOnSaturday.ShouldBeTrue();
    }

    [Test]
    public async Task BuildAsync_PreservesCallerAgentOrder()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 20);

        var contractData = new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            WorkOnMonday = true,
        };

        StubContractData(_ => new Dictionary<Guid, EffectiveContractData>
            {
                [thirdId] = contractData,
                [firstId] = contractData,
                [secondId] = contractData,
            });

        var result = await _sut.BuildAsync(
            new[] { firstId, secondId, thirdId }, date, date, date, date,
            new Dictionary<Guid, double>(),
            groupId: null, CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(
            new[] { firstId.ToString(), secondId.ToString(), thirdId.ToString() });
    }

    [Test]
    public async Task BuildAsync_ContractStartsMidPeriod_UsesFirstActiveDayAsAgentBasis_AndPlansTheDaysBeforeWithTheSettingsDefaults()
    {
        var agentId = Guid.NewGuid();
        var from = new DateOnly(2026, 6, 1);
        var until = new DateOnly(2026, 6, 7);
        var contractStart = new DateOnly(2026, 6, 6);

        var fallbackData = new EffectiveContractData
        {
            HasActiveContract = false,
            WorkOnMonday = true,
            WorkOnFriday = false,
        };

        var contractData = new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            FullTime = 40,
            GuaranteedHours = 160,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };

        StubContractData(date => new Dictionary<Guid, EffectiveContractData>
        {
            [agentId] = date >= contractStart ? contractData : fallbackData,
        });

        var result = await _sut.BuildAsync(
            new[] { agentId }, from, until, from, until,
            new Dictionary<Guid, double>(),
            groupId: null, CancellationToken.None);

        var agent = result.Agents.Single();
        agent.PerformsShiftWork.ShouldBeTrue();
        agent.GuaranteedHours.ShouldBe(160);
        agent.WorkOnMonday.ShouldBeTrue();

        result.ContractDays.Single(d => d.Date == new DateOnly(2026, 6, 1)).WorksOnDay.ShouldBeTrue(
            "a member Monday before the contract uses the settings defaults, whose Monday flag is set");
        result.ContractDays.Single(d => d.Date == new DateOnly(2026, 6, 5)).WorksOnDay.ShouldBeFalse(
            "the settings defaults do not tick Friday");
        result.ContractDays.Single(d => d.Date == contractStart).WorksOnDay.ShouldBeTrue();
        result.ContractDays.Single(d => d.Date == until).WorksOnDay.ShouldBeTrue();
    }

    [Test]
    public async Task BuildAsync_AgentWithoutAnyActiveContract_IsPlannedWithTheSettingsDefaults()
    {
        var agentId = Guid.NewGuid();
        var date = new DateOnly(2026, 6, 1);

        StubContractData(_ => new Dictionary<Guid, EffectiveContractData>
        {
            [agentId] = new EffectiveContractData
            {
                HasActiveContract = false,
                GuaranteedHours = 100,
                WorkOnMonday = true,
                WorkOnTuesday = false,
            },
        });

        var result = await _sut.BuildAsync(
            new[] { agentId }, date, date.AddDays(2), date, date.AddDays(2),
            new Dictionary<Guid, double>(),
            groupId: null, CancellationToken.None);

        result.Agents.Single().GuaranteedHours.ShouldBe(100);
        result.ContractDays.Single(d => d.Date == date).WorksOnDay.ShouldBeTrue();
        result.ContractDays.Single(d => d.Date == date.AddDays(1)).WorksOnDay.ShouldBeFalse();
    }

    [Test]
    public async Task BuildAsync_WorksOnDay_RespectsContractFlags()
    {
        var agentId = Guid.NewGuid();
        var monday = new DateOnly(2026, 4, 20);
        var sunday = new DateOnly(2026, 4, 26);

        var contractData = new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            WorkOnMonday = true,
            WorkOnSunday = false,
        };

        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = contractData });

        var result = await _sut.BuildAsync(
            new[] { agentId }, monday, sunday, monday, sunday,
            new Dictionary<Guid, double>(),
            groupId: null, CancellationToken.None);

        result.ContractDays.Single(d => d.Date == monday).WorksOnDay.ShouldBeTrue();
        result.ContractDays.Single(d => d.Date == sunday).WorksOnDay.ShouldBeFalse();
    }
    [Test]
    public async Task BuildAsync_CopiesSurchargeRatesAndTheirRateModes()
    {
        var agentId = Guid.NewGuid();
        var monday = new DateOnly(2026, 4, 20);

        var contractData = new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            WorkOnMonday = true,
            NightRate = 12m,
            HolidayRate = 0.5m,
            WE1Rate = 8m,
            WE2Rate = 0.25m,
            WE3Rate = 3m,
            NightRateMode = SurchargeRateMode.FixedPerShift,
            HolidayRateMode = SurchargeRateMode.Multiplier,
            WE1RateMode = SurchargeRateMode.FixedPerHour,
            WE2RateMode = SurchargeRateMode.Multiplier,
            WE3RateMode = SurchargeRateMode.FixedPerShift,
        };

        StubContractData(_ => new Dictionary<Guid, EffectiveContractData> { [agentId] = contractData });

        var result = await _sut.BuildAsync(
            new[] { agentId }, monday, monday, monday, monday, new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        var agent = result.Agents.Single();
        agent.NightRate.ShouldBe(12m);
        agent.WE1Rate.ShouldBe(8m);
        agent.NightRateMode.ShouldBe(CoreSurchargeRateMode.FixedPerShift);
        agent.HolidayRateMode.ShouldBe(CoreSurchargeRateMode.Multiplier);
        agent.WE1RateMode.ShouldBe(CoreSurchargeRateMode.FixedPerHour);
        agent.WE2RateMode.ShouldBe(CoreSurchargeRateMode.Multiplier);
        agent.WE3RateMode.ShouldBe(CoreSurchargeRateMode.FixedPerShift);
    }

    [Test]
    public async Task BuildAsync_ResolvesTheContractDataOnceForTheWholePeriod()
    {
        var agentId = Guid.NewGuid();
        var from = new DateOnly(2026, 3, 1);
        var until = from.AddDays(30);
        StubContractData(_ => new Dictionary<Guid, EffectiveContractData>
        {
            [agentId] = new EffectiveContractData { HasActiveContract = true, ContractId = Guid.NewGuid(), WorkOnMonday = true },
        });

        await _sut.BuildAsync(
            new[] { agentId }, from, until, from, until, new Dictionary<Guid, double>(), groupId: null, CancellationToken.None);

        await _contractProvider.Received(1).GetEffectiveContractDataForClientsRangeAsync(
            Arg.Any<List<Guid>>(), from, until, Arg.Any<int?>());
        await _contractProvider.DidNotReceiveWithAnyArgs()
            .GetEffectiveContractDataForClientsAsync(default!, default, default);
    }

    /// <summary>
    /// Stubs the range API the builder now uses, expanding a per-day factory over the requested range.
    /// The builder resolves the contract data for the whole period in ONE call; the day-by-day loop it
    /// used to run repeated the same contract, revision and settings queries for every day.
    /// </summary>
    private void StubContractData(Func<DateOnly, Dictionary<Guid, EffectiveContractData>> perDay)
    {
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
                    result[date] = perDay(date);
                }

                return result;
            });
    }
}
