// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PlanningPeriodDiagnosisBuilder, which mirrors the schedule grid: a day before the employment start or after
/// its end is dark, a day outside a group membership is blue and locked in that group, inclusive bounds, an open end allows
/// everything later, an employee without a membership record is not blocked, and sealed days carry their seal kind.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class PlanningPeriodDiagnosisBuilderTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static readonly IReadOnlySet<DateOnly> None = new HashSet<DateOnly>();

    private static PlanningGroupMembershipPeriod Group(string name, DateOnly? from, DateOnly? until) =>
        new(Guid.NewGuid(), name, from, until);

    private static PlanningPeriodDiagnosis Build(
        MembershipWindow? employment,
        IReadOnlyList<PlanningGroupMembershipPeriod> groups,
        DateOnly from,
        DateOnly until,
        IReadOnlySet<DateOnly>? sealedDays = null,
        IReadOnlySet<DateOnly>? closedDays = null) =>
        PlanningPeriodDiagnosisBuilder.Build(
            "Anna Meier", employment, groups, 0, null, false, from, until, sealedDays ?? None, closedDays ?? None);

    [Test]
    public void DayBeforeEmploymentStart_IsOutsideEmployment()
    {
        var diagnosis = Build(new MembershipWindow(new DateOnly(2026, 10, 6), null), [], Day, Day);

        diagnosis.Days.Single().OutsideEmployment.ShouldBeTrue();
        diagnosis.Days.Single().HasFinding.ShouldBeTrue();
    }

    [Test]
    public void EmploymentBoundsAreInclusive_AndAnOpenEndAllowsEverythingLater()
    {
        var window = new MembershipWindow(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7));

        var diagnosis = Build(window, [], new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 8));

        diagnosis.Days.Select(day => day.OutsideEmployment).ShouldBe([true, false, false, false, true]);
        Build(new MembershipWindow(new DateOnly(2020, 1, 1), null), [], Day, Day).Days.Single().OutsideEmployment.ShouldBeFalse();
    }

    [Test]
    public void WithoutAMembershipRecord_NothingIsBlockedForEmployment()
    {
        Build(null, [], Day, Day).Days.Single().OutsideEmployment.ShouldBeFalse();
    }

    [Test]
    public void DayBeforeTheOnlyGroupMembershipStart_IsOutsideTheGroupPeriod()
    {
        var groups = new[] { Group("Bern", new DateOnly(2026, 10, 20), null) };

        var day = Build(new MembershipWindow(new DateOnly(2024, 1, 1), null), groups, Day, Day).Days.Single();

        day.OutsideEmployment.ShouldBeFalse();
        day.OutsideGroupPeriod.ShouldBeTrue();
        day.OutsideSingleGroupNames.ShouldBe(["Bern"]);
        day.HasFinding.ShouldBeTrue();
    }

    [Test]
    public void TheGridShowsBlueOnlyOutsideTheCombinedRange_NotOutsideASingleGroup()
    {
        var groups = new[]
        {
            Group("A", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30)),
            Group("B", new DateOnly(2026, 9, 1), null)
        };
        var gap = new DateOnly(2026, 7, 15);

        var diagnosis = Build(null, groups, gap, gap);

        var day = diagnosis.Days.Single();
        day.OutsideGroupPeriod.ShouldBeFalse();
        day.HasFinding.ShouldBeFalse();
        day.OutsideSingleGroupNames.ShouldBe(["A", "B"]);
        diagnosis.CombinedGroupFrom.ShouldBe(new DateOnly(2026, 1, 1));
        diagnosis.CombinedGroupUntil.ShouldBeNull();
    }

    [Test]
    public void BeforeTheEarliestOfSeveralGroups_IsOutsideTheCombinedRange()
    {
        var groups = new[]
        {
            Group("A", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30)),
            Group("B", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31))
        };

        var days = Build(null, groups, new DateOnly(2025, 12, 31), new DateOnly(2027, 1, 1)).Days;

        days[0].OutsideGroupPeriod.ShouldBeTrue();
        days[^1].OutsideGroupPeriod.ShouldBeTrue();
        Build(null, groups, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1)).Days.Single().OutsideGroupPeriod.ShouldBeFalse();
    }

    [Test]
    public void AnOpenSideInAnyGroup_OpensTheCombinedRange()
    {
        var groups = new[]
        {
            Group("A", null, new DateOnly(2026, 6, 30)),
            Group("B", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31))
        };

        var diagnosis = Build(null, groups, new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 1));

        diagnosis.CombinedGroupFrom.ShouldBeNull();
        diagnosis.Days.Single().OutsideGroupPeriod.ShouldBeFalse();
    }

    [Test]
    public void GroupBoundsWithAMissingSide_AreOpen()
    {
        var groups = new[] { Group("Bern", null, new DateOnly(2026, 10, 4)) };

        var days = Build(null, groups, new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 5)).Days;

        days[0].OutsideGroupPeriod.ShouldBeFalse();
        days[1].OutsideGroupPeriod.ShouldBeTrue();
    }

    [Test]
    public void SealedDays_CarryTheirKind()
    {
        var closed = new HashSet<DateOnly> { Day.AddDays(1) };
        var sealedDays = new HashSet<DateOnly> { Day, Day.AddDays(1) };

        var days = Build(null, [], Day, Day.AddDays(2), sealedDays, closed).Days;

        days[0].Seal.ShouldBe(WorkBookingSealKind.DayApproval);
        days[1].Seal.ShouldBe(WorkBookingSealKind.PeriodClosed);
        days[2].Seal.ShouldBeNull();
        days[2].HasFinding.ShouldBeFalse();
    }

    [Test]
    public void ARangeYieldsOneStatusPerDay_OldestFirst()
    {
        var days = Build(null, [], Day, Day.AddDays(30)).Days;

        days.Count.ShouldBe(31);
        days[0].Date.ShouldBe(Day);
        days[^1].Date.ShouldBe(Day.AddDays(30));
    }
}
