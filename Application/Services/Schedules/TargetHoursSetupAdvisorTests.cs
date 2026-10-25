// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the deterministic findings of the target-hours setup advisor: the way in use and whether it fits, gaps and odd
/// values in the monthly table (only for employees who read it), a percent without effect on a fixed contract, fixed
/// hours on the table interval, a contract a view interval filters, a no-contract employee who should not be 100 %,
/// and the company-wide mode without an employee.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class TargetHoursSetupAdvisorTests
{
    private static readonly DateOnly November = new(2026, 11, 1);
    private static readonly AbsenceHoursSettingsSummary CompanySettings = new(170m, 180m, 8m, (int)PaymentInterval.Monthly);
    private static readonly IReadOnlyDictionary<int, int> Workdays = Enumerable.Range(1, 12).ToDictionary(month => month, _ => 21);
    private static readonly IReadOnlyDictionary<int, decimal> FullTable = Enumerable.Range(1, 12).ToDictionary(month => month, _ => 168m);
    private static readonly IReadOnlyDictionary<int, decimal> EmptyTable = new Dictionary<int, decimal>();

    [Test]
    public void FixedContract_ConsistentSetup_ReportsWayAndNothingToChange()
    {
        var calculation = Calculation(FixedData(160m, 180m), [Contract("Vollzeit 160", PaymentInterval.Monthly, 160m, 180m)], EmptyTable);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, EmptyTable, Workdays, CompanySettings, null);
        var recommendation = TargetHoursSetupAdvisor.BuildRecommendation(findings, calculation);

        findings.Select(finding => finding.Code).ShouldBe([TargetHoursSetupFindingCode.WayInUse, TargetHoursSetupFindingCode.SetupConsistent]);
        findings[0].Statement.ShouldContain("fixed contract hours");
        recommendation.ShouldContain("Nothing needs to change");
    }

    [Test]
    public void FixedContract_WithPercent_ReportsPercentWithoutEffect()
    {
        var calculation = Calculation(FixedData(160m, 180m), [Contract("Vollzeit 160", PaymentInterval.Monthly, 160m, 180m, percent: 50m)], EmptyTable);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, EmptyTable, Workdays, CompanySettings, null);

        findings[0].Code.ShouldBe(TargetHoursSetupFindingCode.PercentWithoutEffect);
        findings[0].Statement.ShouldContain("Weg 2");
        findings[0].WhereToChange.ShouldBe(TargetHoursSetupAdvisor.ContractPlace);
    }

    [Test]
    public void FixedContract_DoesNotReadTheTable_SoTableGapsAreNotReported()
    {
        var partial = new Dictionary<int, decimal> { [1] = 168m };
        var calculation = Calculation(FixedData(160m, 180m), [Contract("Vollzeit 160", PaymentInterval.Monthly, 160m, 180m)], partial);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, partial, Workdays, CompanySettings, null);

        findings.ShouldNotContain(finding => finding.Code == TargetHoursSetupFindingCode.MonthlyRowsIncomplete);
    }

    [Test]
    public void TableIntervalContract_WithFixedHours_IsContradictory()
    {
        var data = FixedData(160m, 180m) with { PaymentInterval = (int)PaymentInterval.MonthlyTargetHours, GuaranteedHours = 184m, GuaranteedHoursSource = ContractValueSource.MonthlyTargetHours, GuaranteedHoursScaledByContractPercent = true, WorkloadPercent = 100m, WorkloadPercentSource = WorkloadPercentSource.ContractPercentDefault, WorkloadRatioBasis = null };
        var calculation = Calculation(data, [Contract("Weg 1 mit Stunden", PaymentInterval.MonthlyTargetHours, 160m, 180m)], FullTable);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, FullTable, Workdays, CompanySettings, null);

        findings[0].Code.ShouldBe(TargetHoursSetupFindingCode.ContradictoryContractFields);
        findings[0].Statement.ShouldContain("160");
    }

    [Test]
    public void InheritingContract_PartialTable_ReportsGapsAndTheSwitchingBasis()
    {
        var partial = new Dictionary<int, decimal> { [1] = 168m, [11] = 176m };
        var data = FixedData(85m, 180m) with { GuaranteedHoursSource = ContractValueSource.MonthlyTargetHours, GuaranteedHoursScaledByContractPercent = true, WorkloadPercent = 50m, WorkloadPercentSource = WorkloadPercentSource.ContractPercent, WorkloadRatioBasis = null };
        var calculation = Calculation(data, [Contract("Teilzeit 50", PaymentInterval.Monthly, null, null, percent: 50m)], partial);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, partial, Workdays, CompanySettings, null);

        findings.Select(finding => finding.Code).ShouldBe(
            [TargetHoursSetupFindingCode.MonthlyRowsIncomplete, TargetHoursSetupFindingCode.WayInUse]);
        findings[0].Statement.ShouldContain("2 of 12 months");
        findings[0].WhereToChange.ShouldBe(TargetHoursSetupAdvisor.MonthlyTargetHoursPlace);
        findings[1].Statement.ShouldContain("Weg 2");
    }

    [Test]
    public void NoContract_DeviatingMonthValue_IsReported()
    {
        var table = Enumerable.Range(1, 12).ToDictionary(month => month, month => month == 3 ? 120m : 168m);
        var calculation = Calculation(NoContractData(), [], table);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, table, Workdays, CompanySettings, null);

        var finding = findings.Single(candidate => candidate.Code == TargetHoursSetupFindingCode.MonthlyRowDeviatesFromWorkdays);
        finding.Statement.ShouldContain("3: 120 h vs 21 workdays x 8 h = 168 h (28.57 %)");
    }

    [Test]
    public void NoContract_DesiredShareBelowFull_RecommendsAContract()
    {
        var calculation = Calculation(NoContractData(), [], FullTable);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, FullTable, Workdays, CompanySettings, 60m);

        var finding = findings.Single(candidate => candidate.Code == TargetHoursSetupFindingCode.NoContractButNotFullWorkload);
        finding.Statement.ShouldContain("workload percent 60");
        finding.Statement.ShouldContain("108 guaranteed hours at a full time of 180");
        finding.WhereToChange.ShouldBe(TargetHoursSetupAdvisor.AssignmentPlace);
    }

    [Test]
    public void NoContract_DesiredFullShare_IsConsistent()
    {
        var calculation = Calculation(NoContractData(), [], FullTable);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, FullTable, Workdays, CompanySettings, 100m);

        findings.Select(finding => finding.Code).ShouldBe([TargetHoursSetupFindingCode.WayInUse, TargetHoursSetupFindingCode.SetupConsistent]);
    }

    [Test]
    public void ContractFilteredByViewInterval_IsReported()
    {
        var weekly = Contract("Weekly 40", PaymentInterval.Weekly, 40m, 42m) with { UsedByCalculation = true, FilteredByPaymentInterval = true };
        var calculation = Calculation(FixedData(40m, 42m) with { PaymentInterval = (int)PaymentInterval.Weekly }, [weekly], EmptyTable);

        var findings = TargetHoursSetupAdvisor.Analyse(calculation, EmptyTable, Workdays, CompanySettings, null);

        findings[0].Code.ShouldBe(TargetHoursSetupFindingCode.ContractFilteredByViewInterval);
        findings[0].Statement.ShouldContain("Weekly 40");
        findings[0].Statement.ShouldContain("Monthly");
    }

    [Test]
    public void CompanyMode_PartialTable_ReportsGaps_AndRecommendationNamesTheTable()
    {
        var partial = new Dictionary<int, decimal> { [1] = 168m };

        var findings = TargetHoursSetupAdvisor.Analyse(null, partial, Workdays, CompanySettings, null);
        var recommendation = TargetHoursSetupAdvisor.BuildRecommendation(findings, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(TargetHoursSetupFindingCode.MonthlyRowsIncomplete);
        recommendation.ShouldStartWith("Company-wide view");
        recommendation.ShouldContain(TargetHoursSetupAdvisor.MonthlyTargetHoursPlace);
    }

    [Test]
    public void CompanyMode_FullTable_IsConsistent()
    {
        var findings = TargetHoursSetupAdvisor.Analyse(null, FullTable, Workdays, CompanySettings, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(TargetHoursSetupFindingCode.SetupConsistent);
    }

    private static EffectiveContractData NoContractData() => new()
    {
        HasActiveContract = false,
        DefaultWorkingHours = 8m,
        GuaranteedHours = 168m,
        GuaranteedHoursSource = ContractValueSource.MonthlyTargetHours,
        FullTime = 180m,
        WorkloadPercent = 100m,
        WorkloadPercentSource = WorkloadPercentSource.MonthlyTargetHoursFullWorkload,
        PaymentInterval = (int)PaymentInterval.Monthly
    };

    private static EffectiveContractData FixedData(decimal guaranteedHours, decimal fullTime) => new()
    {
        HasActiveContract = true,
        ContractId = Guid.NewGuid(),
        DefaultWorkingHours = 8m,
        GuaranteedHours = guaranteedHours,
        GuaranteedHoursSource = ContractValueSource.Contract,
        FullTime = fullTime,
        WorkloadPercent = guaranteedHours / fullTime * 100m,
        WorkloadPercentSource = WorkloadPercentSource.ContractRatio,
        WorkloadRatioBasis = new WorkloadRatioBasis(guaranteedHours, fullTime),
        PaymentInterval = (int)PaymentInterval.Monthly
    };

    private static AbsenceHoursContractSummary Contract(
        string name, PaymentInterval interval, decimal? guaranteedHours, decimal? fullTime, decimal? percent = null) =>
        new(Guid.NewGuid(), name, interval, true, false, guaranteedHours, fullTime, percent, null);

    private static TargetHoursCalculation Calculation(
        EffectiveContractData data, IReadOnlyList<AbsenceHoursContractSummary> contracts, IReadOnlyDictionary<int, decimal> table) => new()
    {
        ClientId = Guid.NewGuid(),
        PeriodStart = November,
        PeriodEnd = new DateOnly(2026, 11, 30),
        ViewInterval = (int)PaymentInterval.Monthly,
        ContractData = data,
        Way = TargetHoursCalculationService.ClassifyWay(data),
        ContractsActiveAtPeriodStart = contracts,
        Settings = CompanySettings,
        MonthlyTargetHoursRow = table.TryGetValue(11, out var row) ? row : null,
        MonthlyTargetHoursOfYear = table,
        WorkdaysPerMonth = Workdays,
        PeriodTargetHours = data.GuaranteedHours
    };
}
