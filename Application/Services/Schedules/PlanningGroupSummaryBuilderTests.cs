// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PlanningGroupSummaryBuilder, the group-level counterpart of the planning-period diagnosis: it counts the
/// employees with at least one checked day outside the employment (dark) and outside the combined group period (blue), with
/// the grid's inclusive bounds and open ends; it names the most common and the earliest group start among the employees
/// whose membership starts after the first checked day, and counts the sealed employee-days per day.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class PlanningGroupSummaryBuilderTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly Until = new(2026, 10, 19);
    private static readonly DateOnly OldEntry = new(2020, 1, 1);
    private static readonly IReadOnlySet<(Guid ClientId, DateOnly Date)> NoSeals = new HashSet<(Guid, DateOnly)>();

    private static PlanningGroupMember Member(
        DateOnly employmentFrom, DateOnly? employmentUntil = null, DateOnly? groupFrom = null, DateOnly? groupUntil = null,
        bool hasGroupItem = true, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), new MembershipWindow(employmentFrom, employmentUntil), groupFrom, groupUntil, hasGroupItem);

    private static PlanningGroupSummary Build(
        IReadOnlyList<PlanningGroupMember> members, IReadOnlySet<(Guid ClientId, DateOnly Date)>? seals = null) =>
        PlanningGroupSummaryBuilder.Build("Bern", From, Until, members, seals ?? NoSeals);

    [Test]
    public void Employees_WithTheMembershipStartingAfterTheFirstDay_AreBlue_AndTheStartsAreRanked()
    {
        var twentieth = new DateOnly(2026, 10, 20);
        var summary = Build(
        [
            Member(OldEntry, groupFrom: twentieth),
            Member(OldEntry, groupFrom: twentieth),
            Member(OldEntry, groupFrom: new DateOnly(2026, 10, 8)),
            Member(OldEntry, groupFrom: new DateOnly(2024, 3, 1)),
        ]);

        summary.PersonCount.ShouldBe(4);
        summary.OutsideGroupPeriodCount.ShouldBe(3);
        summary.LateGroupStartCount.ShouldBe(3);
        summary.MostCommonLateGroupStart.ShouldBe(twentieth);
        summary.MostCommonLateGroupStartCount.ShouldBe(2);
        summary.EarliestLateGroupStart.ShouldBe(new DateOnly(2026, 10, 8));
        summary.OutsideEmploymentCount.ShouldBe(0);
    }

    [Test]
    public void TheGroupStartOnTheFirstDay_IsInclusive_AndAnOpenStartIsNeverBlue()
    {
        var summary = Build(
        [
            Member(OldEntry, groupFrom: From),
            Member(OldEntry, groupFrom: null),
            Member(OldEntry, hasGroupItem: false),
        ]);

        summary.OutsideGroupPeriodCount.ShouldBe(0);
        summary.LateGroupStartCount.ShouldBe(0);
        summary.MostCommonLateGroupStart.ShouldBeNull();
        summary.EarliestLateGroupStart.ShouldBeNull();
    }

    [Test]
    public void AGroupEndBeforeTheLastDay_IsBlue_AndCountedAsAnEarlyEnd()
    {
        var summary = Build(
        [
            Member(OldEntry, groupUntil: new DateOnly(2026, 10, 10)),
            Member(OldEntry, groupUntil: Until),
        ]);

        summary.OutsideGroupPeriodCount.ShouldBe(1);
        summary.EarlyGroupEndCount.ShouldBe(1);
        summary.LateGroupStartCount.ShouldBe(0);
    }

    [Test]
    public void AnEmployeeOutsideBothTheEmploymentAndTheGroupPeriod_IsCountedInBoth()
    {
        var summary = Build([Member(new DateOnly(2026, 10, 10), groupFrom: new DateOnly(2026, 10, 20))]);

        summary.OutsideEmploymentCount.ShouldBe(1);
        summary.OutsideGroupPeriodCount.ShouldBe(1);
    }

    [Test]
    public void EmploymentBounds_AreInclusive_AndAnExitInsideTheRangeIsDark()
    {
        var summary = Build(
        [
            Member(From, Until),
            Member(OldEntry, new DateOnly(2026, 10, 15)),
            Member(new DateOnly(2026, 10, 2)),
            Member(OldEntry),
        ]);

        summary.OutsideEmploymentCount.ShouldBe(2);
    }

    [Test]
    public void SealedEmployeeDays_AreCountedPerDay_OnlyForTheMembersAndInsideTheRange()
    {
        var first = Member(OldEntry);
        var second = Member(OldEntry);
        var seals = new HashSet<(Guid ClientId, DateOnly Date)>
        {
            (first.ClientId, new DateOnly(2026, 10, 5)),
            (second.ClientId, new DateOnly(2026, 10, 5)),
            (first.ClientId, new DateOnly(2026, 10, 6)),
            (Guid.NewGuid(), new DateOnly(2026, 10, 7)),
            (first.ClientId, new DateOnly(2026, 11, 1)),
        };

        var summary = Build([first, second], seals);

        summary.SealedDays.Select(day => (day.Date, day.PersonCount)).ShouldBe(
            [(new DateOnly(2026, 10, 5), 2), (new DateOnly(2026, 10, 6), 1)]);
    }

    [Test]
    public void WithoutMembers_EverythingIsZero()
    {
        var summary = Build([]);

        summary.PersonCount.ShouldBe(0);
        summary.OutsideEmploymentCount.ShouldBe(0);
        summary.OutsideGroupPeriodCount.ShouldBe(0);
        summary.SealedDays.ShouldBeEmpty();
    }

    [Test]
    public void TheMostCommonStartTie_GoesToTheEarlierDate()
    {
        var summary = Build(
        [
            Member(OldEntry, groupFrom: new DateOnly(2026, 10, 20)),
            Member(OldEntry, groupFrom: new DateOnly(2026, 10, 12)),
        ]);

        summary.MostCommonLateGroupStart.ShouldBe(new DateOnly(2026, 10, 12));
    }
}
