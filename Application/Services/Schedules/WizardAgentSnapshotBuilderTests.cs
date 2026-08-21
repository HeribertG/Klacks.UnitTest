// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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
    private WizardAgentSnapshotBuilder _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _sut = new WizardAgentSnapshotBuilder(_contractProvider);
    }

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
            new[] { agentId }, from, until,
            new Dictionary<Guid, double>(),
            CancellationToken.None);

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
            new[] { agentId }, date, date,
            new Dictionary<Guid, double> { [agentId] = 12.5 },
            CancellationToken.None);

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
            new[] { firstId, secondId, thirdId }, date, date,
            new Dictionary<Guid, double>(),
            CancellationToken.None);

        result.Agents.Select(a => a.Id).ShouldBe(
            new[] { firstId.ToString(), secondId.ToString(), thirdId.ToString() });
    }

    [Test]
    public async Task BuildAsync_ContractStartsMidPeriod_UsesFirstActiveDayAsAgentBasis()
    {
        var agentId = Guid.NewGuid();
        var from = new DateOnly(2026, 6, 1);
        var until = new DateOnly(2026, 6, 7);
        var contractStart = new DateOnly(2026, 6, 6);

        var fallbackData = new EffectiveContractData
        {
            HasActiveContract = false,
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
            new[] { agentId }, from, until,
            new Dictionary<Guid, double>(),
            CancellationToken.None);

        var agent = result.Agents.Single();
        agent.PerformsShiftWork.ShouldBeTrue();
        agent.GuaranteedHours.ShouldBe(160);
        agent.WorkOnMonday.ShouldBeTrue();

        result.ContractDays.Single(d => d.Date == new DateOnly(2026, 6, 1)).WorksOnDay.ShouldBeFalse();
        result.ContractDays.Single(d => d.Date == new DateOnly(2026, 6, 5)).WorksOnDay.ShouldBeFalse();
        result.ContractDays.Single(d => d.Date == contractStart).WorksOnDay.ShouldBeTrue();
        result.ContractDays.Single(d => d.Date == until).WorksOnDay.ShouldBeTrue();
    }

    [Test]
    public async Task BuildAsync_AgentWithoutAnyActiveContract_IsExcluded()
    {
        var agentId = Guid.NewGuid();
        var date = new DateOnly(2026, 6, 1);

        StubContractData(_ => new Dictionary<Guid, EffectiveContractData>
        {
            [agentId] = new EffectiveContractData { HasActiveContract = false },
        });

        var result = await _sut.BuildAsync(
            new[] { agentId }, date, date.AddDays(2),
            new Dictionary<Guid, double>(),
            CancellationToken.None);

        result.Agents.ShouldBeEmpty();
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
            new[] { agentId }, monday, sunday,
            new Dictionary<Guid, double>(),
            CancellationToken.None);

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
            new[] { agentId }, monday, monday, new Dictionary<Guid, double>(), CancellationToken.None);

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
            new[] { agentId }, from, until, new Dictionary<Guid, double>(), CancellationToken.None);

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
