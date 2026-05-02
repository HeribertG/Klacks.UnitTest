// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
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

        _contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData> { [agentId] = contractData });

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

        _contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData> { [agentId] = contractData });

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

        _contractProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData> { [agentId] = contractData });

        var result = await _sut.BuildAsync(
            new[] { agentId }, monday, sunday,
            new Dictionary<Guid, double>(),
            CancellationToken.None);

        result.ContractDays.Single(d => d.Date == monday).WorksOnDay.ShouldBeTrue();
        result.ContractDays.Single(d => d.Date == sunday).WorksOnDay.ShouldBeFalse();
    }
}
