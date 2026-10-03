// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the two known deviations of the CounterRuleEvaluator adapter from the frozen pre-adapter evaluator,
/// both inherited from the shared counter (RuleDay): ShiftExceedingHours keeps only the four longest segments of
/// a day, and durations are compared in whole minutes. Neither occurs with real shift data (five overlong
/// shifts on one day, clock times with seconds); a test turning red here means the shared counter changed.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class CounterRuleAdapterKnownLimitsTests
{
    private static readonly DateOnly Day = new(2026, 7, 15);

    private DataBaseContext _context = null!;
    private ICounterRuleRepository _ruleRepository = null!;
    private LegacyCounterRuleEvaluatorOracle _legacy = null!;
    private CounterRuleEvaluator _adapter = null!;
    private Guid _clientId;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _ruleRepository = Substitute.For<ICounterRuleRepository>();
        var enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Warn);
        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        _legacy = new LegacyCounterRuleEvaluatorOracle(_ruleRepository, _context, enforcementResolver, contractDataProvider);
        _adapter = new CounterRuleEvaluator(_ruleRepository, _context, enforcementResolver, contractDataProvider);
        _clientId = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task FiveOverlongSegmentsOnOneDay_AdapterCountsOnlyTheFourLongest()
    {
        StubOverlongRule(threshold: 5, hoursThreshold: 1m);
        for (var hour = 0; hour < 5; hour++)
        {
            SeedWork(new TimeOnly(hour * 4, 0), new TimeOnly((hour * 4) + 2, 0));
        }

        (await _legacy.EvaluateAsync(_clientId, "Anna", Day)).ShouldHaveSingleItem();
        (await _adapter.EvaluateAsync(_clientId, "Anna", Day)).ShouldBeEmpty();
    }

    [Test]
    public async Task SecondsAboveTheHoursThreshold_AdapterComparesWholeMinutes()
    {
        StubOverlongRule(threshold: 1, hoursThreshold: 13m);
        SeedWork(new TimeOnly(8, 0, 0), new TimeOnly(21, 0, 30));

        (await _legacy.EvaluateAsync(_clientId, "Anna", Day)).ShouldHaveSingleItem();
        (await _adapter.EvaluateAsync(_clientId, "Anna", Day)).ShouldBeEmpty();
    }

    private void StubOverlongRule(int threshold, decimal hoursThreshold)
    {
        _ruleRepository.GetAllActiveAsync().Returns(_ => new List<CounterRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                EventType = CounterEventType.ShiftExceedingHours,
                Period = CounterPeriod.Month,
                Threshold = threshold,
                HoursThreshold = hoursThreshold,
            },
        });
    }

    private void SeedWork(TimeOnly start, TimeOnly end)
    {
        _context.Work.Add(new Klacks.Api.Domain.Models.Schedules.Work
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            StartTime = start,
            EndTime = end,
            WorkTime = 2m,
        });
        _context.SaveChanges();
    }
}
