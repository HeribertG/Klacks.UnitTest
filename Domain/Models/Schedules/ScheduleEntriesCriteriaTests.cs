// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Models.Schedules;

[TestFixture]
public class ScheduleEntriesCriteriaTests
{
    private static readonly DateOnly Day = new(2026, 3, 1);

    [Test]
    public void RefreshWindowAround_SpansTheDayBeforeTheDayAndTheDayAfter_AcrossAMonthEnd()
    {
        var clientId = Guid.NewGuid();
        var groups = new List<Guid> { Guid.NewGuid() };
        var token = Guid.NewGuid();

        var criteria = ScheduleEntriesCriteria.RefreshWindowAround(Day, [clientId], groups, token);

        criteria.From.ShouldBe(new DateOnly(2026, 2, 28));
        criteria.Until.ShouldBe(new DateOnly(2026, 3, 2));
        criteria.ClientIds.ShouldBe([clientId]);
        criteria.VisibleGroupIds.ShouldBeSameAs(groups);
        criteria.AnalyseToken.ShouldBe(token);
        criteria.EntryType.ShouldBeNull();
        criteria.OrderByStartTime.ShouldBeFalse();
    }

    [Test]
    public void RefreshWindowAround_KeepsAMissingGroupListAndTheRealPlan()
    {
        var criteria = ScheduleEntriesCriteria.RefreshWindowAround(Day, [Guid.NewGuid()], null, null);

        criteria.VisibleGroupIds.ShouldBeNull();
        criteria.AnalyseToken.ShouldBeNull();
    }

    [Test]
    public void NewCriteria_DoNotNarrowByClientTypeOrOrder()
    {
        var criteria = new ScheduleEntriesCriteria(Day, Day);

        criteria.ClientIds.ShouldBeNull();
        criteria.EntryType.ShouldBeNull();
        criteria.OrderByStartTime.ShouldBeFalse();
        criteria.VisibleGroupIds.ShouldBeNull();
        criteria.AnalyseToken.ShouldBeNull();
    }
}
