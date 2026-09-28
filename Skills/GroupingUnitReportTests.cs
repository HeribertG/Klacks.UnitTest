// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the planning-unit section of the grouping report: only units with a gap are listed, the most
/// severe first, at most MaxListedPlanningUnits with correct totals, a group-restricted caller sees only
/// units in scope, a dominant blocking reason becomes a data line (code and counts), and the summary text
/// names each unit's numbers, the capacity shortfall per weekday and the sentence that group changes do not
/// fix missing contracts, unfillable duties or capacity gaps.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupingUnitReportTests
{
    private static readonly DateOnly From = new(2026, 9, 28);
    private static readonly Guid East = Guid.NewGuid();
    private static readonly Guid West = Guid.NewGuid();
    private static readonly Guid Foreign = Guid.NewGuid();

    private static GroupingFeasibilityReport Report(params GroupingUnitSummary[] units)
    {
        var names = new Dictionary<Guid, string> { [East] = "Deutschschweiz Ost", [West] = "West", [Foreign] = "Foreign" };
        foreach (var unit in units.Where(unit => !names.ContainsKey(unit.UnitId)))
        {
            names[unit.UnitId] = $"Unit {names.Count:D2}";
        }

        return new GroupingFeasibilityReport(
            new GroupingAnalysisRequest(From, From.AddDays(GroupingFeasibilityDefaults.DefaultHorizonDays), null),
            [],
            [],
            new string('a', 64),
            "r",
            names,
            names.Keys.ToDictionary(id => id, id => (IReadOnlyList<Guid>)[id]),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            0,
            0)
        {
            UnitSummaries = units,
        };
    }

    private static GroupingUnitSummary OstWithoutContracts() => new(
        East, 196, 196, 0, 246, 246, GroupingIneligibilityReason.NoActiveContract, 246,
        [
            new GroupingCapacityGap(DayOfWeek.Monday, 88, 1),
            new GroupingCapacityGap(DayOfWeek.Saturday, 24, 1),
        ]);

    private static GroupingUnitSummary Healthy(Guid id) => new(id, 10, 0, 0, 12, 0, null, 0, []);

    [Test]
    public void OnlyUnitsWithGapsAreListed_WithTotalsOverAllUnits()
    {
        var section = GroupingUnitReportBuilder.Build(Report(OstWithoutContracts(), Healthy(West)), GroupScopeAccess.Unrestricted());

        var unit = section.Units.ShouldHaveSingleItem();
        unit.Unit.ShouldBe("Deutschschweiz Ost");
        unit.DutiesAnalysed.ShouldBe(196);
        unit.DutiesNobodyInUnitCanTake.ShouldBe(196);
        unit.DutiesNobodyInCompanyCanTake.ShouldBe(0);
        unit.EmployeesInScope.ShouldBe(246);
        unit.EmployeesWithoutActiveContract.ShouldBe(246);
        unit.CapacityShortfalls.Select(gap => (gap.Weekday, gap.Demand, gap.Supply))
            .ShouldBe([("Monday", 88, 1), ("Saturday", 24, 1)]);
        section.Totals.ShouldBe(new GroupingUnitTotals(2, 1, 0));
    }

    [Test]
    public void DominantReason_BecomesABlockingCauseDataLine()
    {
        var section = GroupingUnitReportBuilder.Build(Report(OstWithoutContracts()), GroupScopeAccess.Unrestricted());

        section.BlockingCauses.ShouldHaveSingleItem()
            .ShouldStartWith("reason=NoActiveContract employees=246/246 unit=\"Deutschschweiz Ost\"");
    }

    [Test]
    public void ListedUnits_AreCappedAndOrderedBySeverity_TotalsStayCorrect()
    {
        var units = Enumerable.Range(0, GroupingFeasibilityDefaults.MaxListedPlanningUnits + 3)
            .Select(i => new GroupingUnitSummary(Guid.NewGuid(), 5, i, 0, 5, 0, null, 0, []))
            .ToArray();

        var section = GroupingUnitReportBuilder.Build(Report(units), GroupScopeAccess.Unrestricted());

        section.Units.Count.ShouldBe(GroupingFeasibilityDefaults.MaxListedPlanningUnits);
        section.Units[0].DutiesNobodyInUnitCanTake.ShouldBe(GroupingFeasibilityDefaults.MaxListedPlanningUnits + 2);
        section.Units.Select(unit => unit.DutiesNobodyInUnitCanTake).ShouldBeInOrder(SortDirection.Descending);
        section.Totals.ShouldBe(new GroupingUnitTotals(
            GroupingFeasibilityDefaults.MaxListedPlanningUnits + 3,
            GroupingFeasibilityDefaults.MaxListedPlanningUnits + 2,
            2));
    }

    [Test]
    public void RestrictedCaller_SeesOnlyUnitsInScope()
    {
        var foreignGap = new GroupingUnitSummary(Foreign, 3, 3, 0, 2, 2, GroupingIneligibilityReason.NoActiveContract, 2, []);

        var section = GroupingUnitReportBuilder.Build(
            Report(OstWithoutContracts(), foreignGap),
            GroupScopeAccess.Restricted([East], ["Deutschschweiz Ost"]));

        section.Units.Select(unit => unit.Unit).ShouldBe(["Deutschschweiz Ost"]);
        section.BlockingCauses.ShouldAllBe(line => !line.Contains("Foreign"));
        section.Totals.PlanningUnits.ShouldBe(1);
    }

    [Test]
    public void SummaryText_NamesTheUnitsGaps_AndThatGroupChangesDoNotFixThem()
    {
        var view = GroupingReportViewBuilder.Build(Report(OstWithoutContracts()), GroupScopeAccess.Unrestricted(), null);

        var text = GroupingReportTexts.Summary(view);

        text.ShouldContain("\"Deutschschweiz Ost\"");
        text.ShouldContain("196 duties analysed");
        text.ShouldContain("246 of them without an active contract in the period");
        text.ShouldContain("Monday (demand 88, available 1)");
        text.ShouldContain("reason=NoActiveContract employees=246/246");
        text.ShouldContain(GroupingReportTexts.MasterDataSentence);
    }

    [Test]
    public void SummaryText_WithoutGaps_HasNoMasterDataSentence()
    {
        var view = GroupingReportViewBuilder.Build(Report(Healthy(East)), GroupScopeAccess.Unrestricted(), null);

        GroupingReportTexts.Summary(view).ShouldNotContain(GroupingReportTexts.MasterDataSentence);
        view.PlanningUnits.Units.ShouldBeEmpty();
    }
}
