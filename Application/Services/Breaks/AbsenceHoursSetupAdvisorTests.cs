// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the deterministic findings of the absence-hours setup advisor: no contract, contract ignored by the payment
/// interval, 0 guaranteed hours by design, percent field contradicting the hours ratio, scheduling rule overriding
/// the contract, desired hours per day and the consistent case. The advisor is pure, so every test builds a
/// calculation record and asserts codes, places and the recommendation.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Breaks;
using Klacks.Api.Domain.Models.Macros;

namespace Klacks.UnitTest.Application.Services.Breaks;

[TestFixture]
public class AbsenceHoursSetupAdvisorTests
{
    private static readonly DateOnly Wednesday = new(2026, 11, 4);
    private static readonly Guid VacationId = Guid.NewGuid();
    private static readonly Guid MacroId = Guid.NewGuid();
    private static readonly AbsenceHoursSettingsSummary CompanySettings = new(170m, 180m, 8m, (int)PaymentInterval.Monthly);

    [Test]
    public void NoContract_RecommendsAssigningAContract()
    {
        var calculation = Calculation(NoContractData(), []);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);
        var recommendation = AbsenceHoursSetupAdvisor.BuildRecommendation(findings, calculation);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.NoContract);
        findings[0].WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.AssignmentPlace);
        recommendation.ShouldContain("7.56 h");
        recommendation.ShouldContain("Nothing has been changed");
    }

    [Test]
    public void NoContract_MonthRow_ReportsFullWorkloadWithoutContract()
    {
        var data = NoContractData() with
        {
            WorkloadPercent = 100m,
            WorkloadPercentSource = WorkloadPercentSource.MonthlyTargetHoursFullWorkload,
            WorkloadRatioBasis = null,
            GuaranteedHours = 184m,
            GuaranteedHoursSource = ContractValueSource.MonthlyTargetHours
        };
        var calculation = Calculation(data, [], resultHours: 8m) with { MonthlyTargetHoursRow = 184m };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.MonthlyRowGivesFullWorkloadWithoutContract);
    }

    [Test]
    public void ContractOnOtherInterval_IsReportedAsIgnored_AndNoSecondContractIsAdvised()
    {
        var weekly = Contract("Weekly 40", PaymentInterval.Weekly, usedByCalculation: false, guaranteedHours: 40m, fullTime: 42m);
        var calculation = Calculation(NoContractData(), [weekly]);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        var finding = findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe(AbsenceHoursSetupFindingCode.ContractIgnoredByPaymentInterval);
        finding.Statement.ShouldContain("Weekly 40");
        finding.Statement.ShouldContain(nameof(PaymentInterval.Monthly));
        finding.Statement.ShouldContain("do not assign a second contract");
        finding.WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.ContractPlace);
    }

    [Test]
    public void ContractOnOtherInterval_StoredEntryFinding_WarnsThatARecalculationReadsTheContract()
    {
        var weekly = Contract("Weekly 40", PaymentInterval.Weekly, usedByCalculation: false, guaranteedHours: 40m, fullTime: 42m);
        var calculation = Calculation(NoContractData(), [weekly]) with
        {
            ExistingEntry = new AbsenceHoursExistingEntry(Guid.NewGuid(), VacationId, 7.62m, false, WorkLockLevel.None)
        };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.Single(finding => finding.Code == AbsenceHoursSetupFindingCode.StoredEntryDiffersFromCalculation)
            .Statement.ShouldContain("regardless of the payment interval");
    }

    [Test]
    public void ZeroGuaranteedHoursContract_IsByDesign()
    {
        var onCall = Contract("Teilzeit 0 Std", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 0m, fullTime: 180m);
        var data = ContractData(0m, 180m, 0m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [onCall], resultHours: 0m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.ZeroGuaranteedHoursByDesign);
        findings[0].WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.ContractPlace);
    }

    [Test]
    public void PercentField_OnExplicitContract_IsReportedAsIgnored()
    {
        var contract = Contract("Vollzeit 160", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 160m, fullTime: 180m, percent: 50m);
        var data = ContractData(160m, 180m, 160m / 180m * 100m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [contract], resultHours: 7.11m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.PercentFieldIgnoredOnExplicitPath);
        findings[0].Statement.ShouldContain("50");
        findings[0].Statement.ShouldContain("88.89");
    }

    [Test]
    public void PercentField_OnInheritingContract_IsNotAContradiction()
    {
        var contract = Contract("Teilzeit 50", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: null, fullTime: null, percent: 50m);
        var data = ContractData(85m, 180m, 50m, WorkloadPercentSource.ContractPercent) with { WorkloadRatioBasis = null };
        var calculation = Calculation(data, [contract], resultHours: 4m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.SetupConsistent);
    }

    [Test]
    public void SchedulingRule_OverridingHoursAndDailyHours_IsReported()
    {
        var contract = Contract("Vollzeit 160", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 160m, fullTime: 180m);
        var data = ContractData(150m, 180m, 150m / 180m * 100m, WorkloadPercentSource.SchedulingRuleRatio) with
        {
            DefaultWorkingHours = 8.4m,
            DefaultWorkingHoursSource = ContractValueSource.SchedulingRule,
            GuaranteedHoursSource = ContractValueSource.SchedulingRule
        };
        var calculation = Calculation(data, [contract], resultHours: 7m) with
        {
            SchedulingRule = new AbsenceHoursSchedulingRuleSummary(Guid.NewGuid(), "Pflege CH", 150m, null, 8.4m)
        };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.Select(finding => finding.Code).ShouldBe(
        [
            AbsenceHoursSetupFindingCode.SchedulingRuleOverridesContractHours,
            AbsenceHoursSetupFindingCode.SchedulingRuleSetsDailyHours
        ]);
        findings[0].Statement.ShouldContain("Pflege CH");
        findings[0].WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.SchedulingRulePlace);
    }

    [Test]
    public void ImplausibleBasis_IsReported()
    {
        var contract = Contract("Kaputt", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 172.2m, fullTime: 45m);
        var data = ContractData(172.2m, 45m, 100m, WorkloadPercentSource.ImplausibleMasterDataFallback) with { WorkloadRatioBasis = null };
        var calculation = Calculation(data, [contract], resultHours: 8m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.ImplausibleWorkloadBasis);
    }

    [Test]
    public void NoMacro_IsReportedFirst()
    {
        var calculation = Calculation(NoContractData(), [], resultHours: null) with { MacroId = null, MacroExecuted = false };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings[0].Code.ShouldBe(AbsenceHoursSetupFindingCode.NoMacroOnAbsenceType);
        findings[0].WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.AbsenceTypePlace);
    }

    [Test]
    public void StoredEntry_DifferingFromCalculation_PointsToRecalculation()
    {
        var contract = Contract("Vollzeit 160", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 160m, fullTime: 180m);
        var data = ContractData(160m, 180m, 160m / 180m * 100m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [contract], resultHours: 7.11m) with
        {
            ExistingEntry = new AbsenceHoursExistingEntry(Guid.NewGuid(), VacationId, 8m, false, WorkLockLevel.Closed)
        };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        var finding = findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe(AbsenceHoursSetupFindingCode.StoredEntryDiffersFromCalculation);
        finding.Statement.ShouldContain(nameof(WorkLockLevel.Closed));
        finding.WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.RecalculationPlace);
    }

    [Test]
    public void StoredEntry_WithDirectlyRecordedDuration_IsLeftAlone()
    {
        var contract = Contract("Vollzeit 160", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 160m, fullTime: 180m);
        var data = ContractData(160m, 180m, 160m / 180m * 100m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [contract], resultHours: 7.11m) with
        {
            ExistingEntry = new AbsenceHoursExistingEntry(Guid.NewGuid(), VacationId, 4m, true, WorkLockLevel.None)
        };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.SetupConsistent);
    }

    [Test]
    public void DesiredHours_AlreadyReached_SaysSo()
    {
        var contract = Contract("Vollzeit 180", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 180m, fullTime: 180m);
        var data = ContractData(180m, 180m, 100m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [contract], resultHours: 8m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, 8m);
        var recommendation = AbsenceHoursSetupAdvisor.BuildRecommendation(findings, calculation);

        findings.Select(finding => finding.Code).ShouldBe(
            [AbsenceHoursSetupFindingCode.DesiredHoursReached, AbsenceHoursSetupFindingCode.SetupConsistent]);
        recommendation.ShouldContain("Nothing needs to change");
    }

    [Test]
    public void DesiredHours_OnExplicitContract_NamesGuaranteedHoursOrDailyHours()
    {
        var contract = Contract("Vollzeit 160", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 160m, fullTime: 180m);
        var data = ContractData(160m, 180m, 160m / 180m * 100m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [contract], resultHours: 7.11m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, 8m);

        var finding = findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe(AbsenceHoursSetupFindingCode.DesiredHoursNeedChange);
        finding.Statement.ShouldContain("guaranteed hours to 180");
        finding.Statement.ShouldContain("daily working hours");
        finding.WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.ContractPlace);
    }

    [Test]
    public void DesiredHours_OnInheritingContract_NamesThePercentField()
    {
        var contract = Contract("Teilzeit 50", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: null, fullTime: null, percent: 50m);
        var data = ContractData(85m, 180m, 50m, WorkloadPercentSource.ContractPercent) with { WorkloadRatioBasis = null };
        var calculation = Calculation(data, [contract], resultHours: 4m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, 6m);

        findings.ShouldHaveSingleItem().Statement.ShouldContain("workload percent field to 75");
    }

    [Test]
    public void DesiredHours_WithoutContract_PrefersAssigningAContract()
    {
        var calculation = Calculation(NoContractData(), []);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, 8m);

        findings.Select(finding => finding.Code).ShouldBe(
            [AbsenceHoursSetupFindingCode.DesiredHoursNeedChange, AbsenceHoursSetupFindingCode.NoContract]);
        findings[0].Statement.ShouldContain("assign a contract with guaranteed hours 180");
        findings[0].WhereToChange.ShouldBe(AbsenceHoursSetupAdvisor.AssignmentPlace);
    }

    [Test]
    public void ConsistentSetup_RecommendsNothing()
    {
        var contract = Contract("Vollzeit 180", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 180m, fullTime: 180m);
        var data = ContractData(180m, 180m, 100m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [contract], resultHours: 8m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);
        var recommendation = AbsenceHoursSetupAdvisor.BuildRecommendation(findings, calculation);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.SetupConsistent);
        recommendation.ShouldContain("Nothing needs to change");
    }

    [Test]
    public void DesiredHours_OnZeroHoursContract_DoesNotDivideByZero_AndPointsToGuaranteedHours()
    {
        var onCall = Contract("Teilzeit 0 Std", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 0m, fullTime: 180m);
        var data = ContractData(0m, 180m, 0m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [onCall], resultHours: 0m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, 8m);

        findings.Select(finding => finding.Code).ShouldBe(
            [AbsenceHoursSetupFindingCode.DesiredHoursNeedChange, AbsenceHoursSetupFindingCode.ZeroGuaranteedHoursByDesign]);
        findings[0].Statement.ShouldContain("guaranteed hours to 180");
        findings[0].Statement.ShouldContain("no daily-hours value can reach the wish");
    }

    [Test]
    public void InheritingContract_WithRuleGuaranteedHours_IsNotReportedAsOverridden()
    {
        var contract = Contract("Teilzeit 50", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: null, fullTime: null, percent: 50m);
        var data = ContractData(85m, 180m, 50m, WorkloadPercentSource.ContractPercent) with
        {
            WorkloadRatioBasis = null,
            GuaranteedHoursSource = ContractValueSource.Settings,
            GuaranteedHoursScaledByContractPercent = true
        };
        var calculation = Calculation(data, [contract], resultHours: 4m) with
        {
            SchedulingRule = new AbsenceHoursSchedulingRuleSummary(Guid.NewGuid(), "Pflege CH", 150m, null, null)
        };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);

        findings.ShouldHaveSingleItem().Code.ShouldBe(AbsenceHoursSetupFindingCode.SetupConsistent);
    }

    [Test]
    public void OnlyInformationalFindings_StillRecommendNothing()
    {
        var contract = Contract("Vollzeit 180", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 180m, fullTime: 180m);
        var data = ContractData(180m, 180m, 100m, WorkloadPercentSource.ContractRatio) with
        {
            DefaultWorkingHours = 8.4m,
            DefaultWorkingHoursSource = ContractValueSource.SchedulingRule
        };
        var calculation = Calculation(data, [contract], resultHours: 8.4m) with
        {
            SchedulingRule = new AbsenceHoursSchedulingRuleSummary(Guid.NewGuid(), "Pflege CH", null, null, 8.4m)
        };

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, null);
        var recommendation = AbsenceHoursSetupAdvisor.BuildRecommendation(findings, calculation);

        findings.Select(finding => finding.Code).ShouldBe(
            [AbsenceHoursSetupFindingCode.SchedulingRuleSetsDailyHours, AbsenceHoursSetupFindingCode.SetupConsistent]);
        recommendation.ShouldContain("Nothing needs to change");
    }

    [Test]
    public void DesiredHours_WhenTheMacroAppliesItsOwnRule_UsesTheRealResult_AndSaysSo()
    {
        var contract = Contract("Vollzeit 180", PaymentInterval.Monthly, usedByCalculation: true, guaranteedHours: 180m, fullTime: 180m);
        var data = ContractData(180m, 180m, 100m, WorkloadPercentSource.ContractRatio);
        var calculation = Calculation(data, [contract], resultHours: 4m);

        var findings = AbsenceHoursSetupAdvisor.Analyse(calculation, 8m);

        var finding = findings.First();
        finding.Code.ShouldBe(AbsenceHoursSetupFindingCode.DesiredHoursNeedChange);
        finding.Statement.ShouldContain("credits 4 h instead of 8 h");
        finding.Statement.ShouldContain("applies its own rule");
    }

    private static EffectiveContractData NoContractData() => new()
    {
        HasActiveContract = false,
        DefaultWorkingHours = 8m,
        DefaultWorkingHoursSource = ContractValueSource.Settings,
        GuaranteedHours = 170m,
        GuaranteedHoursSource = ContractValueSource.Settings,
        FullTime = 180m,
        WorkloadPercent = 170m / 180m * 100m,
        WorkloadPercentSource = WorkloadPercentSource.SettingsRatio,
        WorkloadRatioBasis = new WorkloadRatioBasis(170m, 180m),
        PaymentInterval = (int)PaymentInterval.Monthly
    };

    private static EffectiveContractData ContractData(
        decimal guaranteedHours, decimal fullTime, decimal percent, WorkloadPercentSource source) => new()
    {
        HasActiveContract = true,
        ContractId = Guid.NewGuid(),
        DefaultWorkingHours = 8m,
        DefaultWorkingHoursSource = ContractValueSource.Settings,
        GuaranteedHours = guaranteedHours,
        GuaranteedHoursSource = ContractValueSource.Contract,
        FullTime = fullTime,
        WorkloadPercent = percent,
        WorkloadPercentSource = source,
        WorkloadRatioBasis = new WorkloadRatioBasis(guaranteedHours, fullTime),
        PaymentInterval = (int)PaymentInterval.Monthly
    };

    private static AbsenceHoursContractSummary Contract(
        string name, PaymentInterval interval, bool usedByCalculation, decimal? guaranteedHours, decimal? fullTime, decimal? percent = null) =>
        new(Guid.NewGuid(), name, interval, usedByCalculation, guaranteedHours, fullTime, percent, null);

    private static AbsenceHoursCalculation Calculation(
        EffectiveContractData data, IReadOnlyList<AbsenceHoursContractSummary> contracts, decimal? resultHours = 7.56m) => new()
    {
        ClientId = Guid.NewGuid(),
        Date = Wednesday,
        AbsenceId = VacationId,
        AbsenceName = new MultiLanguage { De = "Ferien", En = "Vacation" },
        MacroId = MacroId,
        MacroName = "Vacation",
        PaymentIntervalUsed = (int)PaymentInterval.Monthly,
        ContractData = data,
        ContractsActiveOnDate = contracts,
        Settings = CompanySettings,
        MacroInput = new MacroData
        {
            Hour = data.DefaultWorkingHours,
            Weekday = 3,
            GuaranteedHours = data.GuaranteedHours,
            FullTime = data.FullTime,
            WorkloadPercent = data.WorkloadPercent,
            WeekendDay1 = 6,
            WeekendDay2 = 7
        },
        IsWeekendDay = false,
        MacroExecuted = resultHours.HasValue,
        ResultHours = resultHours
    };
}
