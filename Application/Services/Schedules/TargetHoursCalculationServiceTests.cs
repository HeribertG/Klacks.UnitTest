// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that the target-hours calculation reads the SAME pipeline as the schedule row header and get_period_hours:
/// the period boundaries and the prorated period target come from IPeriodHoursService, the provenance from the
/// contract data provider resolved at the period start WITHOUT a view-interval filter, and the way classification
/// mirrors the resolver's flags.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class TargetHoursCalculationServiceTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 30);
    private static readonly Guid ClientId = Guid.NewGuid();

    private IPeriodHoursService _periodHours = null!;
    private IClientContractDataProvider _provider = null!;
    private IEmployeeContractFactsLoader _facts = null!;
    private TargetHoursCalculationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _periodHours = Substitute.For<IPeriodHoursService>();
        _periodHours.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Start, End));
        _periodHours.GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Start, End, null)
            .Returns(new Dictionary<Guid, PeriodHoursResource> { [ClientId] = new() { Hours = 100m, GuaranteedHours = 92m } });

        _provider = Substitute.For<IClientContractDataProvider>();
        _provider.GetEffectiveContractDataAsync(ClientId, Start, null).Returns(new EffectiveContractData
        {
            HasActiveContract = true,
            ContractId = Guid.NewGuid(),
            GuaranteedHours = 184m,
            GuaranteedHoursSource = ContractValueSource.MonthlyTargetHours,
            GuaranteedHoursScaledByContractPercent = true,
            WorkloadPercent = 100m,
            PaymentInterval = (int)PaymentInterval.MonthlyTargetHours
        });

        _facts = Substitute.For<IEmployeeContractFactsLoader>();
        _facts.LoadSettingsAsync(Arg.Any<CancellationToken>()).Returns(new AbsenceHoursSettingsSummary(170m, 180m, 8m, (int)PaymentInterval.Monthly));
        _facts.LoadMonthlyRowsOfYearAsync(2026).Returns(new Dictionary<int, decimal> { [11] = 184m });
        _facts.CountWorkdaysPerMonthAsync(2026, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 12).ToDictionary(month => month, _ => 21));
        _facts.SummariseContractsAsync(ClientId, Start, Arg.Any<Guid?>(), Arg.Any<int?>()).Returns([]);

        _sut = new TargetHoursCalculationService(_periodHours, _provider, _facts);
    }

    [Test]
    public async Task Calculate_UsesThePeriodHoursTarget_AndResolvesProvenanceAtPeriodStartWithoutIntervalFilter()
    {
        var calculation = await _sut.CalculateAsync(ClientId, new DateOnly(2026, 11, 17), null);

        calculation.PeriodStart.ShouldBe(Start);
        calculation.PeriodEnd.ShouldBe(End);
        calculation.PeriodTargetHours.ShouldBe(92m);
        calculation.ProratedByMembership.ShouldBeTrue();
        calculation.ContractData.GuaranteedHours.ShouldBe(184m);
        calculation.MonthlyTargetHoursRow.ShouldBe(184m);
        calculation.Way.ShouldBe(TargetHoursWay.MonthlyTableInterval);
        calculation.ViewInterval.ShouldBe((int)PaymentInterval.Monthly);
        await _provider.Received(1).GetEffectiveContractDataAsync(ClientId, Start, null);
        await _facts.Received(1).SummariseContractsAsync(ClientId, Start, Arg.Any<Guid?>(), (int)PaymentInterval.Monthly);
    }

    [Test]
    public async Task Calculate_ExplicitViewInterval_FlagsContractsAgainstIt()
    {
        await _sut.CalculateAsync(ClientId, Start, (int)PaymentInterval.Weekly);

        await _facts.Received(1).SummariseContractsAsync(ClientId, Start, Arg.Any<Guid?>(), (int)PaymentInterval.Weekly);
    }

    [TestCase(false, 2, false, TargetHoursWay.NoContract)]
    [TestCase(true, 2, false, TargetHoursWay.FixedHours)]
    [TestCase(true, 4, true, TargetHoursWay.MonthlyTableInterval)]
    [TestCase(true, 4, false, TargetHoursWay.MonthlyTableInterval)]
    [TestCase(true, 2, true, TargetHoursWay.InheritedCompanyValue)]
    [TestCase(true, 0, true, TargetHoursWay.InheritedCompanyValue)]
    public void ClassifyWay_MirrorsTheResolverFlags(bool hasContract, int interval, bool scaledByPercent, TargetHoursWay expected)
    {
        var data = new EffectiveContractData
        {
            HasActiveContract = hasContract,
            PaymentInterval = interval,
            GuaranteedHoursScaledByContractPercent = scaledByPercent
        };

        TargetHoursCalculationService.ClassifyWay(data).ShouldBe(expected);
    }
}
