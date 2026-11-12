// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins which reads the period-cap evaluation of several people makes, on substituted collaborators: the rule list,
/// each enforcement mode and the employment starts once for all people, the baseline hours once per distinct window
/// with the ids of all people that read it, and no single-person read. When a grouped read leaves a person out, the
/// evaluation throws instead of reading that person alone.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class PeriodCapEvaluatorBatchReadsTests
{
    private const decimal MonthlyCapHours = 100m;
    private const decimal WeeklyAverageCap = 40m;
    private const int RollingWeeks = 4;
    private const int DaysPerWeek = 7;
    private const decimal HoursOverBothCaps = 400m;
    private const decimal PlannedShiftHours = 8m;

    private static readonly DateOnly Day = new(2026, 3, 18);
    private static readonly DateOnly MonthStart = new(2026, 3, 1);
    private static readonly DateOnly MonthEnd = new(2026, 3, 31);
    private static readonly DateOnly RollingStart = Day.AddDays(-((RollingWeeks * DaysPerWeek) - 1));
    private static readonly Guid[] People = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private IPeriodCapRuleRepository _ruleRepository = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private IClientMembershipStartResolver _membershipStartResolver = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private PeriodCapEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _ruleRepository = Substitute.For<IPeriodCapRuleRepository>();
        _ruleRepository.GetAllActiveAsync().Returns(
        [
            new PeriodCapRule { Id = Guid.NewGuid(), Period = PeriodCapPeriod.Month, Scope = PeriodCapScope.TotalHours, CapHours = MonthlyCapHours },
            new PeriodCapRule
            {
                Id = Guid.NewGuid(),
                Period = PeriodCapPeriod.Month,
                Scope = PeriodCapScope.TotalHours,
                RollingWindowWeeks = RollingWeeks,
                MaxAverageWeeklyHours = WeeklyAverageCap,
            },
        ]);

        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _periodHoursService
            .CalculateHoursForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>().ToDictionary(clientId => clientId, _ => HoursOverBothCaps));

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);

        _membershipStartResolver = Substitute.For<IClientMembershipStartResolver>();
        _membershipStartResolver
            .GetValidFromForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>().ToDictionary(clientId => clientId, _ => (DateOnly?)null));

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();

        _evaluator = new PeriodCapEvaluator(
            _ruleRepository,
            _periodHoursService,
            _enforcementResolver,
            _membershipStartResolver,
            _contractDataProvider,
            Substitute.For<IOvertimeConfigResolver>(),
            Substitute.For<IClientWorkHoursProvider>(),
            Substitute.For<IWeekConfiguration>());
    }

    [Test]
    public async Task SeveralPeopleOnOneDay_ReadEachWindowOnceForAll_AndNothingPerPerson()
    {
        // Act
        var entries = await _evaluator.EvaluatePlannedForClientsAsync(PlannedOnDay(People), analyseToken: null);

        // Assert
        entries.Count.ShouldBe(People.Length + People.Length, "every person breaches the month cap and the rolling average");
        await _ruleRepository.Received(1).GetAllActiveAsync();
        await _enforcementResolver.Received(1).GetModeAsync(ComplianceRuleNames.PeriodCap);
        await _enforcementResolver.Received(1).GetModeAsync(ComplianceRuleNames.RollingAverage);
        await _membershipStartResolver.Received(1).GetValidFromForClientsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == People.Length && People.All(ids.Contains)), Arg.Any<CancellationToken>());
        await _periodHoursService.Received(1).CalculateHoursForClientsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == People.Length && People.All(ids.Contains)), MonthStart, MonthEnd, null);
        await _periodHoursService.Received(1).CalculateHoursForClientsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == People.Length && People.All(ids.Contains)), RollingStart, Day, null);
        await _periodHoursService.DidNotReceiveWithAnyArgs().CalculatePeriodHoursAsync(default, default, default, default);
        await _membershipStartResolver.DidNotReceiveWithAnyArgs().GetValidFromAsync(default);
        await _contractDataProvider.DidNotReceiveWithAnyArgs().GetEffectiveContractDataAsync(default, default, default);
    }

    [Test]
    public async Task GroupedBaselineLeavesAPersonOut_TheEvaluationFailsLoudly_AndNothingIsReadPerPerson()
    {
        // Arrange
        var leftOut = People[1];
        _periodHoursService
            .CalculateHoursForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>()
                .Where(clientId => clientId != leftOut)
                .ToDictionary(clientId => clientId, _ => HoursOverBothCaps));

        // Act & Assert
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _evaluator.EvaluatePlannedForClientsAsync(PlannedOnDay(People), analyseToken: null));
        await _periodHoursService.DidNotReceiveWithAnyArgs().CalculatePeriodHoursAsync(default, default, default, default);
    }

    private static List<ClientPlannedHours> PlannedOnDay(IEnumerable<Guid> people)
        => people.Select(clientId => new ClientPlannedHours(clientId, [(Day, PlannedShiftHours)])).ToList();
}
