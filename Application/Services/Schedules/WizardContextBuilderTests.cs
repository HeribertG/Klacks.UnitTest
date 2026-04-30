// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Application.Services.Schedules;
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

        var agentBuilder = new WizardAgentSnapshotBuilder(_contractProvider);
        _sut = new WizardContextBuilder(agentBuilder, _shiftBuilder, _hardBuilder, _periodHours);
    }

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
            .BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
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

        result.PeriodFrom.Should().Be(new DateOnly(2026, 4, 20));
        result.PeriodUntil.Should().Be(new DateOnly(2026, 4, 22));
        result.AnalyseToken.Should().Be(scenarioToken);
        result.Agents.Should().HaveCount(1);
        result.ContractDays.Should().HaveCount(3);
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
        act.Should().ThrowAsync<OperationCanceledException>();
    }
}
